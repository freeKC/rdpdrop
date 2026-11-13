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
/// offered, outside the RDP channel: connection 0 to the host of the session, then a TLS
/// handshake that accepts only the certificate whose SHA-256 the agent gave, then the token,
/// then the files with a <see cref="DirectSender"/>, which opens the other connections to
/// the same address. A background thread does all of it; the events are raised on the UI
/// thread.
/// </summary>
/// <remarks>
/// When connection 0 cannot be made, its handshake fails, its token is refused or a
/// connection breaks before the first acknowledgement, <see cref="FellBack"/> asks the owner
/// to send the files through the channel instead, without anything to do for the user. Once
/// an acknowledgement came, a broken connection fails the transfer
/// (<see cref="Strings.TransferDirectLost"/>), and the agent keeps nothing of it.
/// </remarks>
internal sealed class DirectUpload : IUploadView
{
    /// <summary>Time allowed to reach the port of the agent, from each address and for each connection.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Time allowed for the TLS handshake and the answer to the token.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A transfer whose agent answers nothing for this long stops, as through the channel.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly Control _owner;
    private readonly IReadOnlyList<DropItem> _items;
    private readonly DirectTransferOffer _offer;
    private readonly string _host;
    private readonly int _connections;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private DirectSender? _sender;
    private Socket? _handshake;
    private Timer? _rateTimer;
    private string? _reached;
    private List<(string Path, TransferFile File)> _files = [];
    private TransferListingException? _listingFailure;
    private volatile bool _stopped;

    /// <param name="owner">A control of the UI thread, to raise the events on.</param>
    /// <param name="dragId">The drag whose drop was deferred.</param>
    /// <param name="transferId">The id the agent gave in its dragAck.</param>
    /// <param name="items">The dropped items, in the order of the dragEnter.</param>
    /// <param name="offer">The direct connection of the dragAck, well formed.</param>
    /// <param name="host">The host of the session, from the full address of the .rdp file, without the port.</param>
    /// <param name="connections">The <c>directConnections</c> setting of the client.</param>
    public DirectUpload(Control owner, string dragId, string transferId, IReadOnlyList<DropItem> items, DirectTransferOffer offer, string host, int connections)
    {
        _owner = owner;
        DragId = dragId;
        TransferId = transferId;
        _items = items;
        _offer = offer;
        _host = host;
        _connections = Math.Clamp(Math.Min(offer.Connections ?? 1, connections), 1, DirectFrames.MaxConnections);
    }

    /// <summary>Raised once the files are listed.</summary>
    public event Action<DirectUpload>? Planned;

    /// <summary>Raised after each acknowledgement of the agent, once <see cref="Progress"/> knows it.</summary>
    public event Action<DirectUpload>? Acknowledged;

    /// <summary>
    /// Raised with the relative path of a file whose open and first read take more than
    /// <see cref="DirectSender.FileWaitNotice"/>, and with null once its first block is read.
    /// </summary>
    public event Action<DirectUpload, string?>? FileWaiting;

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

    /// <summary>The address connection 0 reached, as "host:port", once connected.</summary>
    public string? Endpoint { get; private set; }

    /// <summary>The TLS protocol and cipher suite of connection 0, once the handshake is done.</summary>
    public string? Tls { get; private set; }

    /// <summary>The connections that carried the transfer, once it is over.</summary>
    public int ConnectionsUsed { get; private set; }

    public int FileCount { get; private set; }

    public long TotalBytes { get; private set; }

    public TransferProgress? Progress { get; private set; }

    /// <summary>True once the agent acknowledged data. UI thread only.</summary>
    public bool HasAcknowledgement { get; private set; }

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
    /// English, and every connection closes about a second later at most. Raises nothing.
    /// Callable from any thread.
    /// </summary>
    public void Stop(string reason)
    {
        _stopped = true;
        _stop.Cancel();
        Socket? handshake;
        lock (_gate)
        {
            _sender?.Cancel(reason);
            handshake = _sender is null ? _handshake : null;
        }

        // Before the sender runs, a handshake in progress ends at once.
        handshake?.Dispose();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string MegabytesPerSecond(double bytesPerSecond) => Invariant($"{bytesPerSecond / 1_000_000:0.0}");

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
                _rateTimer?.Dispose();
            }
        }
    }

    /// <summary>
    /// Lists the files once the agent accepted connection 0, and shows them. Null when an
    /// item cannot be read; <see cref="_listingFailure"/> then says which.
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
    /// Connection 0: to the host of the session, then to the address hint of the agent when
    /// the host cannot be reached. Null when neither works: the owner was told to fall back.
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
            try
            {
                var socket = ConnectTo(host, _stop.Token);
                _reached = host;
                Endpoint = host + ":" + _offer.Port.ToString(CultureInfo.InvariantCulture);
                return socket;
            }
            catch (IOException ex)
            {
                if (_stopped)
                {
                    return null;
                }

                failures.Add(ex.Message);
            }
        }

        FallBack("cannot connect, " + string.Join("; ", failures), acknowledged: false);
        return null;
    }

    /// <summary>A TCP connection to <paramref name="host"/> within <see cref="ConnectTimeout"/>; an <see cref="IOException"/> says why not.</summary>
    private Socket ConnectTo(string host, CancellationToken stop)
    {
        // No SO_SNDBUF or SO_RCVBUF: setting them turns off the autotuning of TCP on Windows.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            socket.ConnectAsync(host, _offer.Port, timeout.Token).AsTask().GetAwaiter().GetResult();
            socket.ReceiveTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            socket.SendTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            return socket;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            socket.Dispose();
            throw new IOException(ex is OperationCanceledException
                ? Invariant($"{host}:{_offer.Port} did not answer within {ConnectTimeout.TotalSeconds:0} s")
                : Invariant($"{host}:{_offer.Port} refused or unreachable ({ex.Message})"));
        }
    }

    /// <summary>The TLS handshake on <paramref name="socket"/>, the certificate pinned; an <see cref="IOException"/> says why it failed.</summary>
    private SslStream Handshake(Socket socket, string endpoint)
    {
        var tls = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false, (_, certificate, _, _) => IsPinned(certificate, endpoint));
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
            return tls;
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or ObjectDisposedException or SocketException or System.ComponentModel.Win32Exception)
        {
            tls.Dispose();
            throw new IOException($"the TLS handshake with {endpoint} failed ({ex.Message})");
        }
    }

    /// <summary>Connection <paramref name="index"/> above 0, to the address connection 0 reached; called by the sender on a thread of its own.</summary>
    private DirectConnection? OpenExtra(int index, CancellationToken stop)
    {
        if (_stopped || _reached is not { } host)
        {
            return null;
        }

        var socket = ConnectTo(host, stop);
        var tls = Handshake(socket, Endpoint!);
        return new DirectConnection(tls, ready: () => SetIdleTimeouts(socket), abort: socket.Dispose);
    }

    private static void SetIdleTimeouts(Socket socket)
    {
        socket.ReceiveTimeout = (int)IdleTimeout.TotalMilliseconds;
        socket.SendTimeout = (int)IdleTimeout.TotalMilliseconds;
    }

    private void Send(Socket socket)
    {
        lock (_gate)
        {
            _handshake = socket;
        }

        SslStream tls;
        try
        {
            if (_stopped)
            {
                return;
            }

            tls = Handshake(socket, Endpoint!);
        }
        catch (IOException ex)
        {
            if (!_stopped)
            {
                FallBack(ex.Message, acknowledged: false);
            }

            return;
        }

        Tls = Invariant($"{tls.SslProtocol} ({tls.NegotiatedCipherSuite})");
        var sender = new DirectSender(ListFiles, OpenFile, _connections, () => Environment.TickCount64);
        sender.Ready += () => ClientLog.Info($"Transfer {TransferId}: connection 0 to {Endpoint} with {Tls}; the agent accepted the token.");
        sender.Started += () => OnStarted(sender);
        sender.Info += line => ClientLog.Info($"Transfer {TransferId}: {line}");
        sender.FileWaiting += path => Post(() => FileWaiting?.Invoke(this, path));
        sender.Acknowledged += ack => Post(() =>
        {
            HasAcknowledgement = true;
            Progress?.Report(Environment.TickCount64, ack.TotalBytes);
            Acknowledged?.Invoke(this);
        });
        lock (_gate)
        {
            _sender = sender;
            _handshake = null;
            if (_stopped)
            {
                sender.Cancel("The transfer was stopped before it started.");
            }
        }

        var token = Convert.FromHexString(_offer.Token);
        var outcome = sender.Run(new DirectConnection(tls, ready: () => SetIdleTimeouts(socket), abort: socket.Dispose), token, OpenExtra);
        CryptographicOperations.ZeroMemory(token);
        lock (_gate)
        {
            _rateTimer?.Dispose();
            _rateTimer = null;
        }

        ConnectionsUsed = sender.Stats().Connections;
        LogStatistics(sender);
        Report(outcome);
    }

    private FileStream OpenFile(int index) =>
        new(_files[index].Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

    /// <summary>The go frame came: the samples of the rate start, and a line per second at the debug level.</summary>
    private void OnStarted(DirectSender sender)
    {
        ClientLog.Info($"Transfer {TransferId}: the agent checked the list and created the files; the data goes over up to {sender.PlannedConnections} connection(s).");
        var timer = new Timer(
            _ =>
            {
                if (sender.Meter.Sample() is { } rate && ClientLog.DebugEnabled)
                {
                    ClientLog.Debug(Invariant($"Transfer {TransferId}: {MegabytesPerSecond(rate)} MB/s in the last second through the direct connection, {sender.Meter.Bytes} bytes written, {sender.AcknowledgedBytes} acknowledged."));
                }
            },
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        lock (_gate)
        {
            _rateTimer = timer;
        }
    }

    /// <summary>One line at the end: where the time went on this side.</summary>
    private void LogStatistics(DirectSender sender)
    {
        if (sender.PlannedConnections == 0)
        {
            return;
        }

        var stats = sender.Stats();
        var perConnection = string.Join(", ", stats.BytesPerConnection.Select((bytes, index) => (bytes, index)).Where(pair => pair.bytes >= 0).Select(pair => Invariant($"{pair.index}: {pair.bytes}")));
        ClientLog.Info(Invariant(
            $"Transfer {TransferId}: direct connection statistics: {stats.Connections} of {sender.PlannedConnections} planned connection(s), bytes per connection {perConnection}; {stats.Seconds:0.0} s from the go frame; the reader spent {stats.ReadSeconds:0.00} s opening and reading the files and waited {stats.QueueFullSeconds:0.00} s for room in the queue; the writers spent {stats.WriteSeconds:0.00} s in socket writes and waited {stats.QueueEmptySeconds:0.00} s for blocks, summed over the connections; average {MegabytesPerSecond(stats.Rates.Average)} MB/s, first 5 s {MegabytesPerSecond(stats.Rates.First)} MB/s, last 5 s {MegabytesPerSecond(stats.Rates.Last)} MB/s."));
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
    private bool IsPinned(X509Certificate? certificate, string endpoint)
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

        ClientLog.Warn($"Transfer {TransferId}: the certificate of {endpoint} has the SHA-256 {hash}, not the one the agent gave through the channel; the direct connection is refused.");
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
}
