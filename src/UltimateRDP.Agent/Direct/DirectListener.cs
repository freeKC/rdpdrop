using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Timer = System.Threading.Timer;
using UltimateRDP.Agent.Drops;
using UltimateRDP.Agent.Localization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Resources;
using UltimateRDP.Core.Agent;
using UltimateRDP.Core.Direct;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Agent.Direct;

/// <summary>
/// The listener of one direct transfer offer: a TCP port of the <c>directPortRange</c>
/// setting, bound to all addresses, a random token of 32 bytes and the certificate of
/// <see cref="DirectCertificate"/>, offered in the dragAck. It accepts exactly one
/// connection, and stops listening at once after it; a connection that does not finish
/// the TLS handshake and present the token within <see cref="HandshakeTimeout"/> is
/// dropped. After the deferred drop, a listener that got no connection within
/// <see cref="ConnectTimeout"/> closes, and the files come through the channel. The files
/// go to the folder <c>staging\&lt;transferId&gt;-direct</c>, through a
/// <see cref="DirectReceiver"/>.
/// </summary>
/// <remarks>
/// One background thread per offer, from the offer to the end of its transfer; nothing of
/// it exists while no transfer is offered. <see cref="Finished"/> is raised once on that
/// thread: true when every file arrived and was checked. Any other end deletes what came
/// and leaves the drop to the channel.
/// </remarks>
internal sealed class DirectListener
{
    /// <summary>After the deferred drop, the client connects within this time or the listener closes.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A connection finishes the TLS handshake and presents the token within this time, or is dropped.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A transfer that receives nothing for this long stops, as through the channel.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>After the final frame, the client has this long to close.</summary>
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(2);

    private readonly FileLogger _logger;
    private readonly TcpListener _listener;
    private readonly SslStreamCertificateContext _certificate;
    private readonly StagingFolder _staging;
    private readonly ClientLanguage _language;
    private readonly IReadOnlyList<TransferItem> _items;
    private readonly byte[] _token;
    private readonly Lock _gate = new();
    private Timer? _connectTimer;
    private Socket? _socket;
    private bool _closed;
    private int _finished;

    private DirectListener(FileLogger logger, TcpListener listener, int port, SslStreamCertificateContext certificate, string certificateSha256, string dragId, string transferId, IReadOnlyList<TransferItem> items, StagingFolder staging, ClientLanguage language)
    {
        _logger = logger;
        _listener = listener;
        _certificate = certificate;
        _staging = staging;
        _language = language;
        _items = items;
        DragId = dragId;
        TransferId = transferId;
        Port = port;
        _token = RandomNumberGenerator.GetBytes(DirectFrames.TokenLength);
        Offer = new DirectTransferOffer(port, TransferHash.ToHex(_token), certificateSha256, PrimaryIPv4());
        var folder = staging.FolderOf(FolderId);
        StagedPaths = [.. items.Select(item => Path.Combine(folder, item.Name))];
    }

    /// <summary>Raised once on the thread of the listener when it is over: true when every file arrived and was checked.</summary>
    public event Action<DirectListener, bool>? Finished;

    /// <summary>Raised on the thread of the listener for each block of data written: the transfer is alive.</summary>
    public event Action? Alive;

    public string DragId { get; }

    public string TransferId { get; }

    /// <summary>The port the listener took.</summary>
    public int Port { get; }

    /// <summary>What the dragAck offers the client. Its token is never logged.</summary>
    public DirectTransferOffer Offer { get; }

    /// <summary>The staging folder of the files of the direct connection.</summary>
    public string FolderId => TransferId + "-direct";

    /// <summary>Paths of the copies of the dropped items, in the order of the drag.</summary>
    public IReadOnlyList<string> StagedPaths { get; }

    /// <summary>True while the offer stands or its transfer runs.</summary>
    public bool IsActive => Volatile.Read(ref _finished) == 0;

    /// <summary>True once every file arrived through the direct connection and was checked.</summary>
    public bool Succeeded { get; private set; }

    /// <summary>
    /// Starts a listener on the first free port of <paramref name="range"/>. Returns null,
    /// with a line in the log, when no port of the range is free or the certificate cannot
    /// be created.
    /// </summary>
    public static DirectListener? TryStart(FileLogger logger, (int First, int Last) range, string dragId, string transferId, IReadOnlyList<TransferItem> items, StagingFolder staging, ClientLanguage language)
    {
        if (DirectCertificate.Get(logger) is not { } certificate)
        {
            return null;
        }

        for (var port = range.First; port <= range.Last; port++)
        {
            if (Bind(port) is { } listener)
            {
                var direct = new DirectListener(logger, listener, port, certificate.Context, certificate.Sha256, dragId, transferId, items, staging, language);
                direct.Start();
                logger.Info($"Transfer {transferId} of drag {dragId}: direct connection offered on port {port}{(direct.Offer.Host is { } host ? ", address hint " + host : string.Empty)}.");
                return direct;
            }
        }

        logger.Warn($"Transfer {transferId} of drag {dragId}: no port of the range {DirectTransferPolicy.ToSetting(range)} is free, so no direct connection is offered; the files come through the channel.");
        return null;
    }

    /// <summary>
    /// The client dropped: it connects now. Without a connection within
    /// <see cref="ConnectTimeout"/>, the listener closes.
    /// </summary>
    public void OnDeferredDrop()
    {
        lock (_gate)
        {
            if (_closed || _socket is not null || _connectTimer is not null)
            {
                return;
            }

            _connectTimer = new Timer(_ => CloseIfNotConnected(), null, ConnectTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Ends the offer, and the transfer when one runs; what came is deleted. Callable from any thread.</summary>
    public void Close(string reason)
    {
        Socket? socket;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            socket = _socket;
            _connectTimer?.Dispose();
        }

        if (IsActive)
        {
            _logger.Info($"Transfer {TransferId}: direct connection on port {Port} closed, {reason}.");
        }

        StopListening();
        socket?.Dispose();
    }

    /// <summary>Binds a listener to all addresses, IPv6 and IPv4 when the system has IPv6. Null when the port is taken.</summary>
    private static TcpListener? Bind(int port)
    {
        foreach (var address in new[] { IPAddress.IPv6Any, IPAddress.Any })
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(address, port)
                {
                    // No other socket may take the port, even on one address only.
                    ExclusiveAddressUse = true,
                };
                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    listener.Server.DualMode = true;
                }

                listener.Start(backlog: 1);
                return listener;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
            {
                listener?.Stop();
            }
            catch (SocketException)
            {
                listener?.Stop();
                return null;
            }
        }

        return null;
    }

    /// <summary>The first IPv4 address of an interface that is up and has an IPv4 gateway, or null.</summary>
    private static string? PrimaryIPv4()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                if (!properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)))
                {
                    continue;
                }

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
                    {
                        return unicast.Address.ToString();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException)
        {
            // The hint is optional: the client uses the host of its .rdp file first.
        }

        return null;
    }

    private void Start()
    {
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "UltimateRDP direct " + TransferId,
        };
        thread.Start();
    }

    private void CloseIfNotConnected()
    {
        lock (_gate)
        {
            if (_closed || _socket is not null)
            {
                return;
            }
        }

        Close($"no connection came within {ConnectTimeout.TotalSeconds:0} s of the drop; the files come through the channel");
    }

    private void StopListening()
    {
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // Already stopped.
        }
    }

    private void Run()
    {
        var succeeded = false;
        try
        {
            succeeded = Serve();
        }
        catch (Exception ex)
        {
            _logger.Error($"Transfer {TransferId}: unexpected error on the direct connection.", ex);
        }
        finally
        {
            StopListening();
            lock (_gate)
            {
                _closed = true;
                _connectTimer?.Dispose();
            }

            Succeeded = succeeded;
            Volatile.Write(ref _finished, 1);
            try
            {
                Finished?.Invoke(this, succeeded);
            }
            catch (Exception ex)
            {
                _logger.Error($"Transfer {TransferId}: the end handler of the direct connection failed.", ex);
            }
        }
    }

    /// <summary>Accepts the one connection and runs its transfer. True when every file arrived.</summary>
    private bool Serve()
    {
        Socket socket;
        try
        {
            socket = _listener.AcceptSocket();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Closed before any connection: the client sends through the channel, or nothing.
            return false;
        }

        // Exactly one connection per offer.
        StopListening();
        lock (_gate)
        {
            if (_closed)
            {
                socket.Dispose();
                return false;
            }

            _socket = socket;
            _connectTimer?.Dispose();
        }

        var remote = socket.RemoteEndPoint?.ToString() ?? "(unknown address)";
        _logger.Info($"Transfer {TransferId}: direct connection from {remote} on port {Port}.");
        socket.NoDelay = true;
        socket.ReceiveTimeout = (int)HandshakeTimeout.TotalMilliseconds;
        socket.SendTimeout = (int)HandshakeTimeout.TotalMilliseconds;
        using var deadline = new Timer(_ => socket.Dispose(), null, HandshakeTimeout, Timeout.InfiniteTimeSpan);
        using var network = new NetworkStream(socket, ownsSocket: true);
        using var tls = new SslStream(network, leaveInnerStreamOpen: false);
        try
        {
            tls.AuthenticateAsServer(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = _certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            });
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or ObjectDisposedException or SocketException or System.ComponentModel.Win32Exception)
        {
            _logger.Warn($"Transfer {TransferId}: the TLS handshake of the direct connection from {remote} failed, the connection is dropped. {ex.Message}");
            return false;
        }

        var protocol = string.Create(CultureInfo.InvariantCulture, $"{tls.SslProtocol} ({tls.NegotiatedCipherSuite})");
        var storage = _staging.CreateTransferStorage(FolderId);
        var receiver = new DirectReceiver(_token, _items, storage, () => Environment.TickCount64, HasRoomFor);
        CryptographicOperations.ZeroMemory(_token);
        var authenticated = false;
        receiver.Authenticated += () =>
        {
            deadline.Change(Timeout.Infinite, Timeout.Infinite);
            authenticated = true;
            socket.ReceiveTimeout = (int)IdleTimeout.TotalMilliseconds;
            socket.SendTimeout = (int)IdleTimeout.TotalMilliseconds;
            _logger.Info($"Transfer {TransferId}: direct connection from {remote} accepted with {protocol}; receiving the files.");
        };
        receiver.DataReceived += () => Alive?.Invoke();
        receiver.FinalSent += () => socket.ReceiveTimeout = (int)CloseWait.TotalMilliseconds;
        receiver.Run(tls, Describe);
        Summarize(receiver, remote, protocol, authenticated);
        return receiver.State == DirectReceiverState.Succeeded;
    }

    /// <summary>True when the announced bytes, with the margin of the channel path, fit in the staging folder.</summary>
    private bool HasRoomFor(long bytes)
    {
        var free = _staging.FreeSpace();
        var required = FastTransferPolicy.RequiredFreeSpace(bytes);
        if (free is { } available && available < required)
        {
            _logger.Warn($"Transfer {TransferId}: {bytes} bytes announced on the direct connection, {required} bytes of free space needed and {available} free. Refused.");
            return false;
        }

        return true;
    }

    /// <summary>The text of the final frame, in the language of the client.</summary>
    private string? Describe(DirectReceiver receiver)
    {
        var path = receiver.FailurePath;
        LocalText? text = receiver.State switch
        {
            DirectReceiverState.Succeeded => null,
            DirectReceiverState.Cancelled => LocalText.Of(nameof(Strings.TransferCancelled)),
            _ => receiver.Failure switch
            {
                DirectFailure.NoSpace => LocalText.Of(nameof(Strings.TransferNoSpace), StagedTransfer.Megabytes(FastTransferPolicy.RequiredFreeSpace(receiver.ExpectedBytes)), StagedTransfer.Megabytes(_staging.FreeSpace() ?? 0)),
                DirectFailure.HashMismatch or DirectFailure.SizeMismatch when path is not null => LocalText.Of(nameof(Strings.TransferChecksum), path),
                DirectFailure.BadName => LocalText.Of(nameof(Strings.TransferBadName), path),
                DirectFailure.WriteFailed => LocalText.Of(nameof(Strings.TransferWriteFailed), path, receiver.FailureDetail),
                DirectFailure.Incomplete => LocalText.Of(nameof(Strings.TransferIncomplete), path),
                _ => LocalText.Of(nameof(Strings.TransferDataLost)),
            },
        };
        return text?.In(_language.Culture);
    }

    /// <summary>One line at the end, in the words of the channel summary, with the path, the port and the TLS protocol.</summary>
    private void Summarize(DirectReceiver receiver, string remote, string protocol, bool authenticated)
    {
        var seconds = receiver.Milliseconds / 1000.0;
        var rate = seconds > 0 ? receiver.ReceivedBytes / 1_000_000.0 / seconds : 0;
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"Transfer {TransferId} of drag {DragId}: {receiver.State} through the direct connection from {remote} on port {Port}, {protocol}, {receiver.ReceivedFiles} of {receiver.ExpectedFiles} file(s), {receiver.ReceivedBytes} of {receiver.ExpectedBytes} bytes in {seconds:0.0} s, {rate:0.0} MB/s, {receiver.AcksSent} acknowledgement(s)");
        switch (receiver.State)
        {
            case DirectReceiverState.Succeeded:
                _logger.Info(line + ".");
                break;

            case DirectReceiverState.Cancelled:
                _logger.Info($"{line}. Cancelled by the client, {receiver.CancelReason}; the drop waits for what the channel says.");
                break;

            default:
                _logger.Warn(authenticated
                    ? $"{line}. {receiver.Failure}: {receiver.FailureDetail} The files that came are deleted; the drop waits for what the channel says."
                    : $"Transfer {TransferId}: the direct connection from {remote} did not present the token of the offer and is dropped ({receiver.Failure}: {receiver.FailureDetail}).");
                break;
        }
    }
}
