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
/// The listener of one direct transfer offer, version 2 of the protocol: a TCP port of the
/// <c>directPortRange</c> setting, bound to all addresses, a random token of 32 bytes and the
/// certificate of <see cref="DirectCertificate"/>, offered in the dragAck with the number of
/// connections the agent accepts. It keeps listening after the first connection, until the
/// transfer ends or every connection of the offer presented its token, and accepts at most
/// <see cref="ExtraSockets"/> sockets more than that; each one finishes the TLS handshake and
/// presents its token within <see cref="HandshakeTimeout"/>, or is dropped. After the deferred
/// drop, a listener whose connection 0 did not come within <see cref="ConnectTimeout"/>
/// closes, and the files come through the channel. The files go to the folder
/// <c>staging\&lt;transferId&gt;-direct</c>, through a <see cref="DirectReceiver"/>.
/// </summary>
/// <remarks>
/// One background thread per offer, from the offer to the end of its transfer, and one per
/// connection; nothing of it exists while no transfer is offered. <see cref="Finished"/> is
/// raised once on the thread of the offer: true when every file arrived and was checked.
/// Any other end deletes what came and leaves the drop to the channel.
/// </remarks>
internal sealed class DirectListener
{
    /// <summary>After the deferred drop, connection 0 comes within this time or the listener closes.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A connection finishes the TLS handshake and presents its token within this time, or is dropped.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Sockets accepted beyond the connections of the offer, so that a scanner from an allowed address cannot keep the port busy.</summary>
    public const int ExtraSockets = 4;

    /// <summary>A transfer that receives nothing for this long stops, as through the channel.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>After the final frame, the client has this long to close.</summary>
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(2);

    /// <summary>A transfer whose connections closed may be a cancel of the client: its transferCancel through the channel may follow within this time.</summary>
    private static readonly TimeSpan ChannelCancelWait = TimeSpan.FromSeconds(2);

    private readonly FileLogger _logger;
    private readonly TcpListener _listener;
    private readonly SslStreamCertificateContext _certificate;
    private readonly StagingFolder _staging;
    private readonly ClientLanguage _language;
    private readonly DirectReceiver _receiver;
    private readonly Lock _gate = new();
    private readonly List<Socket> _sockets = [];
    private readonly ManualResetEventSlim _clientCancel = new(false);
    private Timer? _connectTimer;
    private Timer? _rateTimer;
    private Socket? _control;
    private string _remote = "(unknown address)";
    private string _protocol = "no TLS session";
    private string? _closeReason;
    private bool _closed;
    private int _accepted;
    private int _finished;
    private int _released;
    private long _stoppedStamp;
    private long _releasedStamp;

    private DirectListener(FileLogger logger, TcpListener listener, int port, SslStreamCertificateContext certificate, string certificateSha256, string dragId, string transferId, IReadOnlyList<TransferItem> items, StagingFolder staging, ClientLanguage language, int maxConnections, int maxMegabytesPerSecond)
    {
        _logger = logger;
        _listener = listener;
        _certificate = certificate;
        _staging = staging;
        _language = language;
        DragId = dragId;
        TransferId = transferId;
        Port = port;
        MaxConnections = maxConnections;
        var token = RandomNumberGenerator.GetBytes(DirectFrames.TokenLength);
        Offer = new DirectTransferOffer(port, TransferHash.ToHex(token), certificateSha256, PrimaryIPv4(), DirectFrames.ProtocolVersion, maxConnections);
        var folder = staging.FolderOf(FolderId);
        StagedPaths = [.. items.Select(item => Path.Combine(folder, item.Name))];
        Pacer = DirectPacer.ForMegabytesPerSecond(maxMegabytesPerSecond, () => Environment.TickCount64);
        _receiver = new DirectReceiver(token, maxConnections, items, staging.CreateDirectStorage(FolderId), () => Environment.TickCount64, Describe, HasRoomFor, Pacer);
        CryptographicOperations.ZeroMemory(token);
        _receiver.DataReceived += () => Alive?.Invoke();
        _receiver.Started += OnStarted;
        _receiver.FinalSent += OnFinalSent;
        _receiver.ConnectionDropped += (index, detail) => _logger.Info($"Transfer {TransferId}: connection {index} closed before it carried any block ({detail}); the transfer goes on without it.");
        _receiver.Stopped += OnStopped;
        _receiver.Finishing += OnFinishing;
    }

    /// <summary>Raised once on the thread of the listener when it is over: true when every file arrived and was checked.</summary>
    public event Action<DirectListener, bool>? Finished;

    /// <summary>Raised on the thread of a connection for each block written: the transfer is alive.</summary>
    public event Action? Alive;

    public string DragId { get; }

    public string TransferId { get; }

    /// <summary>The port the listener took.</summary>
    public int Port { get; }

    /// <summary>The connections the offer accepts.</summary>
    public int MaxConnections { get; }

    /// <summary>The limit of the rate, when the setting asks for one.</summary>
    public DirectPacer? Pacer { get; }

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
    public static DirectListener? TryStart(FileLogger logger, (int First, int Last) range, int maxConnections, int maxMegabytesPerSecond, string dragId, string transferId, IReadOnlyList<TransferItem> items, StagingFolder staging, ClientLanguage language)
    {
        if (DirectCertificate.Get(logger) is not { } certificate)
        {
            return null;
        }

        for (var port = range.First; port <= range.Last; port++)
        {
            if (Bind(port, maxConnections) is { } listener)
            {
                DirectListener direct;
                try
                {
                    direct = new DirectListener(logger, listener, port, certificate.Context, certificate.Sha256, dragId, transferId, items, staging, language, maxConnections, maxMegabytesPerSecond);
                }
                catch (IOException ex)
                {
                    listener.Stop();
                    logger.Warn($"Transfer {transferId} of drag {dragId}: no direct connection is offered, the staging folder cannot hold it; the files come through the channel. {ex.Message}");
                    return null;
                }

                direct.Start();
                logger.Info(string.Create(CultureInfo.InvariantCulture, $"Transfer {transferId} of drag {dragId}: direct connection offered on port {port}, protocol version {DirectFrames.ProtocolVersion}, up to {maxConnections} connection(s){(direct.Pacer is { } pacer ? $", at most {pacer.BytesPerSecond / 1_000_000} MB/s" : string.Empty)}{(direct.Offer.Host is { } host ? ", address hint " + host : string.Empty)}."));
                return direct;
            }
        }

        logger.Warn($"Transfer {transferId} of drag {dragId}: no port of the range {DirectTransferPolicy.ToSetting(range)} is free, so no direct connection is offered; the files come through the channel.");
        return null;
    }

    /// <summary>
    /// The client dropped: it connects now. Without connection 0 within
    /// <see cref="ConnectTimeout"/>, the listener closes.
    /// </summary>
    public void OnDeferredDrop()
    {
        lock (_gate)
        {
            if (_closed || _control is not null || _connectTimer is not null)
            {
                return;
            }

            _connectTimer = new Timer(_ => CloseIfNotConnected(), null, ConnectTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// The client cancelled the transfer through the channel: a transfer that runs ends as
    /// cancelled, and one whose connections just closed is counted as cancelled. Callable
    /// from any thread.
    /// </summary>
    public void OnClientCancel(string reason)
    {
        _receiver.ClientCancelled(reason);
        _clientCancel.Set();
    }

    /// <summary>Ends the offer, and the transfer when one runs; what came is deleted. Callable from any thread.</summary>
    public void Close(string reason)
    {
        Socket[] sockets;
        bool started;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _closeReason = reason;
            started = _control is not null;
            sockets = [.. _sockets];
            _connectTimer?.Dispose();
        }

        if (IsActive && !started)
        {
            _logger.Info($"Transfer {TransferId}: direct connection on port {Port} closed, {reason}.");
        }

        _receiver.Close(reason);
        StopListening();

        // The sockets still in their handshake; the receiver closed the others.
        foreach (var socket in sockets)
        {
            socket.Dispose();
        }
    }

    /// <summary>Binds a listener to all addresses, IPv6 and IPv4 when the system has IPv6. Null when the port is taken.</summary>
    internal static TcpListener? Bind(int port, int backlog)
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

                listener.Start(backlog);
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
    internal static string? PrimaryIPv4()
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

    private static string MegabytesPerSecond(double bytesPerSecond) => (bytesPerSecond / 1_000_000).ToString("0.0", CultureInfo.InvariantCulture);

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
            if (_closed || _control is not null)
            {
                return;
            }
        }

        Close($"connection 0 did not come within {ConnectTimeout.TotalSeconds:0} s of the drop; the files come through the channel");
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
        try
        {
            Accept();

            // The transfer always ends: by its final frame, a lost connection, the timeouts of
            // the sockets, or Close, which the drag, the drop and the agent call. Its end only
            // waits for its connections: the files close and delete afterwards.
            _receiver.WaitStopped(Timeout.InfiniteTimeSpan);
            if (_receiver.State == DirectReceiverState.Succeeded)
            {
                // The copies are dropped next, so their files must be closed, which the
                // verifier did as it checked them: this wait is short.
                _receiver.WaitEnded(Timeout.InfiniteTimeSpan);
            }

            Release();
            Summarize();
            LogCleanUp();
        }
        catch (Exception ex)
        {
            _logger.Error($"Transfer {TransferId}: unexpected error on the direct connection.", ex);
            _receiver.Close("unexpected error");
        }
        finally
        {
            Release();
        }
    }

    /// <summary>
    /// The direct connection of the session is free again: a new offer may start at once, on
    /// the next free port of the range when this one is still closing. Runs once.
    /// </summary>
    private void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        StopListening();
        lock (_gate)
        {
            _closed = true;
            _connectTimer?.Dispose();
            _rateTimer?.Dispose();
        }

        Succeeded = _receiver.State == DirectReceiverState.Succeeded;
        Interlocked.Exchange(ref _releasedStamp, System.Diagnostics.Stopwatch.GetTimestamp());
        Volatile.Write(ref _finished, 1);
        try
        {
            Finished?.Invoke(this, Succeeded);
        }
        catch (Exception ex)
        {
            _logger.Error($"Transfer {TransferId}: the end handler of the direct connection failed.", ex);
        }
    }

    private void OnStopped()
    {
        Interlocked.Exchange(ref _stoppedStamp, System.Diagnostics.Stopwatch.GetTimestamp());
        StopListening();
    }

    /// <summary>The final frame is about to go out: a client that no longer reads holds it at most a second.</summary>
    private void OnFinishing()
    {
        lock (_gate)
        {
            if (_control is { } control)
            {
                try
                {
                    control.SendTimeout = 1000;
                    control.ReceiveTimeout = (int)CloseWait.TotalMilliseconds;
                }
                catch (ObjectDisposedException)
                {
                    // Closed already.
                }
            }
        }
    }

    /// <summary>Milliseconds from the end of the transfer to the release of the direct connection.</summary>
    private long ReleaseMilliseconds()
    {
        var stopped = Interlocked.Read(ref _stoppedStamp);
        var released = Interlocked.Read(ref _releasedStamp);
        return stopped == 0 || released < stopped ? 0 : (released - stopped) * 1000 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>One line once the files are closed, and deleted unless the transfer succeeded: what the file system took after the end.</summary>
    private void LogCleanUp()
    {
        lock (_gate)
        {
            if (_control is null)
            {
                return;
            }
        }

        _receiver.WaitEnded(Timeout.InfiniteTimeSpan);
        if (_receiver.CleanUpTimes is not { } times)
        {
            return;
        }

        var total = times.WaitMilliseconds + times.CloseMilliseconds + (times.DeleteMilliseconds ?? 0);
        _logger.Info(string.Create(
            CultureInfo.InvariantCulture,
            $"Transfer {TransferId}: the files of the direct connection were {(times.DeleteMilliseconds is null ? "closed" : "closed and deleted")} {total} ms after its end: {times.WaitMilliseconds} ms until no thread wrote or read them, {times.CloseMilliseconds} ms closing them{(times.DeleteMilliseconds is { } delete ? string.Create(CultureInfo.InvariantCulture, $", {delete} ms deleting the folder") : string.Empty)}."));
    }

    /// <summary>
    /// Accepts sockets until the transfer ends, every connection of the offer presented its
    /// token, <see cref="ExtraSockets"/> more than that were accepted, or the offer closes.
    /// </summary>
    private void Accept()
    {
        while (true)
        {
            lock (_gate)
            {
                if (_closed || _receiver.IsFinished || _receiver.Connections >= MaxConnections || _accepted >= MaxConnections + ExtraSockets)
                {
                    break;
                }
            }

            Socket socket;
            try
            {
                socket = _listener.AcceptSocket();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // Closed: the transfer ended, or the offer.
                break;
            }

            int number;
            lock (_gate)
            {
                if (_closed)
                {
                    socket.Dispose();
                    break;
                }

                _sockets.Add(socket);
                number = ++_accepted;
            }

            var thread = new Thread(() => Serve(socket))
            {
                IsBackground = true,
                Name = "UltimateRDP direct " + TransferId + " socket " + number.ToString(CultureInfo.InvariantCulture),
            };
            thread.Start();
        }

        StopListening();
    }

    /// <summary>One socket: the TLS handshake, then its token and its frames through the receiver.</summary>
    private void Serve(Socket socket)
    {
        var remote = socket.RemoteEndPoint?.ToString() ?? "(unknown address)";
        try
        {
            ServeTls(socket, remote);
        }
        catch (Exception ex)
        {
            _logger.Error($"Transfer {TransferId}: unexpected error on a direct connection from {remote}.", ex);
        }
        finally
        {
            lock (_gate)
            {
                _sockets.Remove(socket);
            }

            socket.Dispose();
        }
    }

    private void ServeTls(Socket socket, string remote)
    {
        // No SO_SNDBUF or SO_RCVBUF: setting them turns off the autotuning of TCP on Windows.
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
            if (!_receiver.IsFinished)
            {
                _logger.Warn($"Transfer {TransferId}: the TLS handshake of a direct connection from {remote} failed, the connection is dropped. {ex.Message}");
            }

            return;
        }

        var protocol = string.Create(CultureInfo.InvariantCulture, $"{tls.SslProtocol} ({tls.NegotiatedCipherSuite})");
        var result = _receiver.Serve(tls, index =>
        {
            deadline.Change(Timeout.Infinite, Timeout.Infinite);
            socket.ReceiveTimeout = (int)IdleTimeout.TotalMilliseconds;
            socket.SendTimeout = (int)IdleTimeout.TotalMilliseconds;
            if (index == 0)
            {
                lock (_gate)
                {
                    _control = socket;
                    _remote = remote;
                    _protocol = protocol;
                    _connectTimer?.Dispose();
                }

                _logger.Info($"Transfer {TransferId}: connection 0 from {remote} accepted with {protocol}; receiving the list of the files.");
            }
            else
            {
                _logger.Info($"Transfer {TransferId}: connection {index} from {remote} accepted with {protocol}.");
            }

            if (_receiver.Connections >= MaxConnections)
            {
                StopListening();
            }
        });
        if (result.Refusal is { } refusal && !_receiver.IsFinished)
        {
            _logger.Warn($"Transfer {TransferId}: the direct connection from {remote} did not present a token of the offer and is dropped. {refusal}");
        }
    }

    /// <summary>The data flows: a line per second at the debug level, and the samples of the rates of the summary.</summary>
    private void OnStarted()
    {
        var timer = new Timer(_ => SampleRate(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        lock (_gate)
        {
            _rateTimer = timer;
        }
    }

    private void SampleRate()
    {
        if (_receiver.Meter.Sample() is { } rate && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.Debug(string.Create(CultureInfo.InvariantCulture, $"Transfer {TransferId}: {MegabytesPerSecond(rate)} MB/s in the last second through the direct connection, {_receiver.ReceivedBytes} of {_receiver.ExpectedBytes} bytes over {_receiver.Connections} connection(s)."));
        }
    }

    private void OnFinalSent()
    {
        lock (_gate)
        {
            if (_control is { } control)
            {
                try
                {
                    control.ReceiveTimeout = (int)CloseWait.TotalMilliseconds;
                }
                catch (ObjectDisposedException)
                {
                    // Closed already.
                }
            }
        }
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

    /// <summary>
    /// Two lines at the end: the outcome, in the words of the channel summary, with the path,
    /// the port and the TLS protocol; then where the time went.
    /// </summary>
    private void Summarize()
    {
        string remote;
        string protocol;
        lock (_gate)
        {
            if (_control is null)
            {
                // No transfer started: Close or the refusals said why.
                return;
            }

            remote = _remote;
            protocol = _protocol;
        }

        if (_receiver.State == DirectReceiverState.Failed && _receiver.Failure == DirectFailure.Broken && !_receiver.ClosedByAgent)
        {
            // The connections of a client that cancels close right after its transferCancel,
            // which may come through the channel a little later.
            _clientCancel.Wait(ChannelCancelWait);
        }

        var receiver = _receiver;
        var seconds = receiver.Milliseconds / 1000.0;
        var rate = seconds > 0 ? receiver.ReceivedBytes / seconds : 0;
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"Transfer {TransferId} of drag {DragId}: {receiver.State} through the direct connection from {remote} on port {Port}, {protocol}, {receiver.ReceivedFiles} of {receiver.ExpectedFiles} file(s), {receiver.ReceivedBytes} of {receiver.ExpectedBytes} bytes in {seconds:0.0} s, {MegabytesPerSecond(rate)} MB/s, {receiver.AcksSent} acknowledgement(s), the direct connection free again {ReleaseMilliseconds()} ms after the end");
        switch (receiver.State)
        {
            case DirectReceiverState.Succeeded:
                _logger.Info(line + ".");
                break;

            case DirectReceiverState.Cancelled:
                _logger.Info($"{line}. Cancelled by the client {(receiver.CancelledThroughChannel ? "through the channel" : "with a cancel frame")}, {receiver.CancelReason}; the files that came are being deleted.");
                break;

            case DirectReceiverState.Failed when receiver.ClosedByAgent:
                _logger.Info($"{line}. Closed by the agent, {_closeReason}; the files that came are being deleted.");
                break;

            default:
                _logger.Warn($"{line}. {receiver.Failure}: {receiver.FailureDetail} The files that came are deleted; the drop waits for what the channel says.");
                break;
        }

        var rates = receiver.Meter.Rates();
        var perConnection = string.Join(", ", receiver.BytesPerConnection.Select((bytes, index) => (bytes, index)).Where(pair => pair.bytes >= 0).Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.index}: {pair.bytes}")));
        _logger.Info(string.Create(
            CultureInfo.InvariantCulture,
            $"Transfer {TransferId}: direct connection statistics: {receiver.Connections} connection(s), bytes per connection {perConnection}; socket reads {receiver.ReadSeconds:0.00} s, file writes {receiver.WriteSeconds:0.00} s, hashing {receiver.HashSeconds:0.00} s{(Pacer is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $", waits for the rate limit {receiver.PacedSeconds:0.00} s"))}, summed over the threads; average {MegabytesPerSecond(rates.Average)} MB/s, first 5 s {MegabytesPerSecond(rates.First)} MB/s, last 5 s {MegabytesPerSecond(rates.Last)} MB/s."));
    }
}
