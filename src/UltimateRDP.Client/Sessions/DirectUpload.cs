using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Direct;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;
using Timer = System.Threading.Timer;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// Sends the files of one drop to the agent through the direct connection that its dragAck
/// offered, outside the RDP channel: a TCP connection to the host of the session, then a
/// TLS handshake that accepts only the certificate whose SHA-256 the agent gave, then the
/// token, then the files with a <see cref="DirectSender"/>. A background thread does all of
/// it; the events are raised on the UI thread.
/// </summary>
/// <remarks>
/// When the connection cannot be made, the handshake fails, the token is refused or the
/// stream breaks before the first acknowledgement, <see cref="FellBack"/> asks the owner to
/// send the files through the channel instead, without anything to do for the user. Once
/// an acknowledgement came, a broken stream fails the transfer
/// (<see cref="Strings.TransferDirectLost"/>), and the agent keeps nothing of it.
/// </remarks>
internal sealed class DirectUpload : IUploadView
{
    /// <summary>Time allowed to reach the port of the agent, from each address.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Time allowed for the TLS handshake and the answer to the token.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A transfer whose agent answers nothing for this long stops, as through the channel.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly Control _owner;
    private readonly IReadOnlyList<DropItem> _items;
    private readonly DirectTransferOffer _offer;
    private readonly string _host;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private DirectSender? _sender;
    private Stream? _stream;
    private Timer? _closeTimer;
    private List<(string Path, TransferFile File)> _files = [];
    private TransferListingException? _listingFailure;
    private volatile bool _stopped;

    /// <param name="owner">A control of the UI thread, to raise the events on.</param>
    /// <param name="dragId">The drag whose drop was deferred.</param>
    /// <param name="transferId">The id the agent gave in its dragAck.</param>
    /// <param name="items">The dropped items, in the order of the dragEnter.</param>
    /// <param name="offer">The direct connection of the dragAck, well formed.</param>
    /// <param name="host">The host of the session, from the full address of the .rdp file, without the port.</param>
    public DirectUpload(Control owner, string dragId, string transferId, IReadOnlyList<DropItem> items, DirectTransferOffer offer, string host)
    {
        _owner = owner;
        DragId = dragId;
        TransferId = transferId;
        _items = items;
        _offer = offer;
        _host = host;
    }

    /// <summary>Raised once the files are listed.</summary>
    public event Action<DirectUpload>? Planned;

    /// <summary>Raised after each acknowledgement of the agent, once <see cref="Progress"/> knows it.</summary>
    public event Action<DirectUpload>? Acknowledged;

    /// <summary>Raised when the direct connection cannot be used: the files must go through the channel. The text says why, in English.</summary>
    public event Action<DirectUpload, string>? FellBack;

    /// <summary>Raised when the transfer failed and the files must not go through the channel either.</summary>
    public event Action<DirectUpload, LocalText>? Failed;

    /// <summary>Raised when the agent has every file: the drop of the copies follows through the channel.</summary>
    public event Action<DirectUpload, DirectResult>? Completed;

    public string DragId { get; }

    public string TransferId { get; }

    /// <summary>The dropped items, in the order of the dragEnter, for the channel when it takes over.</summary>
    public IReadOnlyList<DropItem> Items => _items;

    /// <summary>True: the files go through the direct connection.</summary>
    public bool IsDirect => true;

    /// <summary>The port of the agent.</summary>
    public int Port => _offer.Port;

    /// <summary>The address the connection reached, as "host:port", once connected.</summary>
    public string? Endpoint { get; private set; }

    /// <summary>The TLS protocol and cipher suite, once the handshake is done.</summary>
    public string? Tls { get; private set; }

    public int FileCount { get; private set; }

    public long TotalBytes { get; private set; }

    public TransferProgress? Progress { get; private set; }

    public IReadOnlyList<TransferFile> Files { get; private set; } = [];

    public IReadOnlyList<long> FileSizes { get; private set; } = [];

    public long StartTicks { get; } = Environment.TickCount64;

    /// <summary>Starts the background thread.</summary>
    public void Start()
    {
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "UltimateRDP direct " + TransferId,
        };
        thread.Start();
    }

    /// <summary>
    /// Stops the transfer: the agent gets a cancel frame with <paramref name="reason"/>, in
    /// English, and the connection closes a few seconds later at most. Raises nothing.
    /// Callable from any thread.
    /// </summary>
    public void Stop(string reason)
    {
        _stopped = true;
        _stop.Cancel();
        lock (_gate)
        {
            _sender?.Cancel(reason);
            if (_stream is { } stream && _closeTimer is null)
            {
                // A write blocked by a full connection does not see the cancel: the close ends it.
                _closeTimer = new Timer(_ => stream.Dispose(), null, DirectSender.CancelWait + TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Run()
    {
        try
        {
            if (Connect() is not { } socket)
            {
                return;
            }

            using (socket)
            {
                Send(socket);
            }
        }
        catch (Exception ex)
        {
            // An error here must not take the client down: the channel takes the files instead.
            ClientLog.Error($"Transfer {TransferId}: unexpected error on the direct connection.", ex);
            FallBack("unexpected error " + ex.GetType().Name + ": " + ex.Message, acknowledged: false);
        }
        finally
        {
            lock (_gate)
            {
                _closeTimer?.Dispose();
            }
        }
    }

    /// <summary>
    /// Lists the files once the agent accepted the token, and shows them. Null when an item
    /// cannot be read; <see cref="_listingFailure"/> then says which.
    /// </summary>
    private IReadOnlyList<TransferFile>? ListFiles()
    {
        try
        {
            _files = TransferListing.List(_items);
        }
        catch (TransferListingException ex)
        {
            ClientLog.Warn($"Transfer {TransferId}: cannot list {ex.ItemPath}. {ex.Message}");
            _listingFailure = ex;
            return null;
        }

        TransferFile[] planned = [.. _files.Select(file => file.File)];
        var total = planned.Sum(file => file.Size);
        Post(() =>
        {
            FileCount = planned.Length;
            TotalBytes = total;
            Files = planned;
            FileSizes = [.. planned.Select(file => file.Size)];
            Progress = new TransferProgress(total, Environment.TickCount64);
            Planned?.Invoke(this);
        });
        return planned;
    }

    /// <summary>
    /// Connects to the host of the session, then to the address hint of the agent when the
    /// host cannot be reached. Null when neither works: the owner was told to fall back.
    /// </summary>
    private Socket? Connect()
    {
        var failures = new List<string>(2);
        var hosts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(_host))
        {
            hosts.Add(_host);
        }

        if (!string.IsNullOrWhiteSpace(_offer.Host) && !string.Equals(_offer.Host, _host, StringComparison.OrdinalIgnoreCase))
        {
            hosts.Add(_offer.Host);
        }

        foreach (var host in hosts)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                socket.ConnectAsync(host, _offer.Port, timeout.Token).AsTask().GetAwaiter().GetResult();
                Endpoint = host + ":" + _offer.Port.ToString(CultureInfo.InvariantCulture);
                return socket;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
            {
                socket.Dispose();
                if (_stopped)
                {
                    return null;
                }

                failures.Add(ex is OperationCanceledException
                    ? Invariant($"{host}:{_offer.Port} did not answer within {ConnectTimeout.TotalSeconds:0} s")
                    : Invariant($"{host}:{_offer.Port} refused or unreachable ({ex.Message})"));
            }
        }

        FallBack("cannot connect, " + string.Join("; ", failures), acknowledged: false);
        return null;
    }

    private void Send(Socket socket)
    {
        socket.ReceiveTimeout = (int)HandshakeTimeout.TotalMilliseconds;
        socket.SendTimeout = (int)HandshakeTimeout.TotalMilliseconds;
        using var network = new NetworkStream(socket, ownsSocket: false);
        using var tls = new SslStream(network, leaveInnerStreamOpen: false, ValidatePinned);
        try
        {
            tls.AuthenticateAsClient(new SslClientAuthenticationOptions
            {
                // The name is not checked: the certificate is pinned by its SHA-256.
                TargetHost = "UltimateRDP agent",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            });
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or ObjectDisposedException or SocketException or System.ComponentModel.Win32Exception)
        {
            if (!_stopped)
            {
                FallBack($"the TLS handshake with {Endpoint} failed ({ex.Message})", acknowledged: false);
            }

            return;
        }

        Tls = Invariant($"{tls.SslProtocol} ({tls.NegotiatedCipherSuite})");
        var sender = new DirectSender(ListFiles, index => new FileStream(_files[index].Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan));
        sender.Ready += () =>
        {
            socket.ReceiveTimeout = (int)IdleTimeout.TotalMilliseconds;
            socket.SendTimeout = (int)IdleTimeout.TotalMilliseconds;
            ClientLog.Info($"Transfer {TransferId}: direct connection to {Endpoint} with {Tls}; the agent accepted the token.");
        };
        sender.Acknowledged += ack => Post(() =>
        {
            Progress?.Report(Environment.TickCount64, ack.TotalBytes);
            Acknowledged?.Invoke(this);
        });
        lock (_gate)
        {
            _sender = sender;
            _stream = tls;
            if (_stopped)
            {
                sender.Cancel("The transfer was stopped before it started.");
            }
        }

        var token = Convert.FromHexString(_offer.Token);
        var outcome = sender.Run(tls, token);
        CryptographicOperations.ZeroMemory(token);
        Report(outcome);
    }

    /// <summary>Tells the owner how the transfer ended.</summary>
    private void Report(DirectSendOutcome outcome)
    {
        if (_stopped || outcome.Status == DirectSendStatus.Cancelled)
        {
            ClientLog.Info($"Transfer {TransferId}: the direct connection stopped, {outcome.Detail}");
            return;
        }

        switch (outcome.Status)
        {
            case DirectSendStatus.Succeeded:
                Post(() => Completed?.Invoke(this, outcome.Result!));
                break;

            case DirectSendStatus.AgentFailed or DirectSendStatus.AgentCancelled:
                ClientLog.Warn($"Transfer {TransferId}: the agent ended the transfer on the direct connection, {outcome.Result?.Failure}: {outcome.Result?.Message}");
                var message = outcome.Result?.Message;
                Post(() => Failed?.Invoke(this, string.IsNullOrWhiteSpace(message)
                    ? LocalText.Of(nameof(Strings.TransferFailedNoDetail))
                    : LocalText.Of(outcome.Status == DirectSendStatus.AgentCancelled ? nameof(Strings.CancelledReason) : nameof(Strings.TransferFailedReason), message)));
                break;

            case DirectSendStatus.LocalFailure when _listingFailure is { } listing:
                Post(() => Failed?.Invoke(this, LocalText.Of(nameof(Strings.TransferReadFailed), listing.ItemPath, listing.Message)));
                break;

            case DirectSendStatus.LocalFailure:
                var path = _files.FirstOrDefault(file => file.File.RelativePath == outcome.Path).Path ?? outcome.Path ?? string.Empty;
                ClientLog.Warn($"Transfer {TransferId}: {outcome.Detail}");
                Post(() => Failed?.Invoke(this, outcome.FileChanged
                    ? LocalText.Of(nameof(Strings.TransferFileChanged), path)
                    : LocalText.Of(nameof(Strings.TransferReadFailed), path, outcome.Detail)));
                break;

            default:
                // Refused, or broken: before the first acknowledgement the channel takes the files.
                FallBack(Invariant($"{outcome.Status} on {Endpoint}, {outcome.Detail}"), outcome.Acknowledged);
                break;
        }
    }

    private void FallBack(string reason, bool acknowledged)
    {
        if (_stopped)
        {
            return;
        }

        if (acknowledged)
        {
            ClientLog.Warn($"Transfer {TransferId}: the direct connection was lost after the agent acknowledged data, {reason}");
            Post(() => Failed?.Invoke(this, LocalText.Of(nameof(Strings.TransferDirectLost))));
            return;
        }

        Post(() => FellBack?.Invoke(this, reason));
    }

    /// <summary>Accepts the certificate of the agent only: its SHA-256 is the one of the dragAck. The chain and the name are not checked.</summary>
    private bool ValidatePinned(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return false;
        }

        var hash = TransferHash.ToHex(certificate.GetCertHash(HashAlgorithmName.SHA256));
        if (string.Equals(hash, _offer.CertSha256, StringComparison.Ordinal))
        {
            return true;
        }

        ClientLog.Warn($"Transfer {TransferId}: the certificate of {Endpoint} has the SHA-256 {hash}, not the one the agent gave through the channel; the direct connection is refused.");
        return false;
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread, after the actions posted before.</summary>
    private void Post(Action action)
    {
        if (_stopped)
        {
            return;
        }

        try
        {
            _owner.BeginInvoke(() =>
            {
                if (!_stopped)
                {
                    action();
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The session view is gone: nothing is left to report to.
            _stopped = true;
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
