using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Core.Agent;
using UltimateRDP.Core.Direct;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;
using Timer = System.Threading.Timer;

namespace UltimateRDP.Agent.Direct;

/// <summary>
/// The listener of the files that the session sends to the client, since 0.9.0: the same
/// port range, certificate, token and frames as <see cref="DirectListener"/>, with the roles
/// reversed. The agent still listens and the client still connects, but its token frames say
/// that it receives (<see cref="DirectRole.ClientReceives"/>), and the agent runs a
/// <see cref="DirectSender"/> on the connections it accepted: connection 0 first, then the
/// others as they come. The data is paced by <c>directMaxMegabytesPerSecond</c>, since the
/// outbound link of the server also carries the screens of the other sessions.
/// </summary>
/// <remarks>
/// One background thread per offer, from the offer to the end of its transfer, and one per
/// socket for its handshake; connection 0 keeps its thread for the transfer. Nothing of it
/// exists while nothing is sent. <see cref="Finished"/> is raised once with the outcome, or
/// with null when no transfer started.
/// </remarks>
internal sealed class DirectSendListener
{
    /// <summary>After the client accepted the offer, connection 0 comes within this time or the listener closes.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly FileLogger _logger;
    private readonly TcpListener _listener;
    private readonly SslStreamCertificateContext _certificate;
    private readonly DirectTokenGate _gate;
    private readonly IReadOnlyList<(string Path, TransferFile File)> _files;
    private readonly DirectSender _sender;
    private readonly Lock _lock = new();
    private readonly List<Socket> _handshakes = [];
    private Timer? _connectTimer;
    private Timer? _rateTimer;
    private string _remote = "(unknown address)";
    private string _protocol = "no TLS session";
    private bool _closed;
    private bool _started;
    private int _accepted;
    private int _finished;

    private DirectSendListener(FileLogger logger, TcpListener listener, int port, SslStreamCertificateContext certificate, string certificateSha256, string sendId, IReadOnlyList<(string Path, TransferFile File)> files, int maxConnections, int maxMegabytesPerSecond)
    {
        _logger = logger;
        _listener = listener;
        _certificate = certificate;
        _files = files;
        SendId = sendId;
        Port = port;
        MaxConnections = maxConnections;
        var token = RandomNumberGenerator.GetBytes(DirectFrames.TokenLength);
        Offer = new DirectTransferOffer(port, TransferHash.ToHex(token), certificateSha256, DirectListener.PrimaryIPv4(), DirectFrames.ProtocolVersion, maxConnections);
        _gate = new DirectTokenGate(token, maxConnections, DirectRole.ClientReceives);
        CryptographicOperations.ZeroMemory(token);
        Pacer = DirectPacer.ForMegabytesPerSecond(maxMegabytesPerSecond, () => Environment.TickCount64);
        _sender = new DirectSender(() => [.. _files.Select(file => file.File)], OpenFile, maxConnections, () => Environment.TickCount64)
        {
            Pacer = Pacer,
            Peer = "client",
        };
        _sender.Info += line => _logger.Info($"Send {SendId}: {line}");
        _sender.Started += OnStarted;
    }

    /// <summary>Raised once on a thread of the listener when it is over: the outcome of the transfer, or null when none started.</summary>
    public event Action<DirectSendListener, DirectSendOutcome?>? Finished;

    /// <summary>Raised after each acknowledgement of the client.</summary>
    public event Action<DirectAck>? Acknowledged
    {
        add => _sender.Acknowledged += value;
        remove => _sender.Acknowledged -= value;
    }

    public string SendId { get; }

    public int Port { get; }

    public int MaxConnections { get; }

    public DirectPacer? Pacer { get; }

    /// <summary>What the sendOffer gives the client. Its token is never logged.</summary>
    public DirectTransferOffer Offer { get; }

    /// <summary>True while the offer stands or its transfer runs.</summary>
    public bool IsActive => Volatile.Read(ref _finished) == 0;

    /// <summary>
    /// Starts a listener on the first free port of <paramref name="range"/>. Returns null, with
    /// a line in the log, when no port is free or the certificate cannot be created.
    /// </summary>
    public static DirectSendListener? TryStart(FileLogger logger, (int First, int Last) range, int maxConnections, int maxMegabytesPerSecond, string sendId, IReadOnlyList<(string Path, TransferFile File)> files)
    {
        if (DirectCertificate.Get(logger) is not { } certificate)
        {
            return null;
        }

        for (var port = range.First; port <= range.Last; port++)
        {
            if (DirectListener.Bind(port, maxConnections) is { } listener)
            {
                var direct = new DirectSendListener(logger, listener, port, certificate.Context, certificate.Sha256, sendId, files, maxConnections, maxMegabytesPerSecond);
                direct.Start();
                logger.Info(string.Create(CultureInfo.InvariantCulture, $"Send {sendId}: direct connection offered on port {port} for the client to receive, protocol version {DirectFrames.ProtocolVersion}, up to {maxConnections} connection(s){(direct.Pacer is { } pacer ? $", at most {pacer.BytesPerSecond / 1_000_000} MB/s" : string.Empty)}{(direct.Offer.Host is { } host ? ", address hint " + host : string.Empty)}."));
                return direct;
            }
        }

        logger.Warn($"Send {sendId}: no port of the range {DirectTransferPolicy.ToSetting(range)} is free, so the files cannot be sent.");
        return null;
    }

    /// <summary>The client accepted the offer: connection 0 must come within <see cref="ConnectTimeout"/>.</summary>
    public void OnAccepted()
    {
        lock (_lock)
        {
            if (_closed || _started || _connectTimer is not null)
            {
                return;
            }

            _connectTimer = new Timer(_ => CloseIfNotConnected(), null, ConnectTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Ends the offer, and the transfer when one runs. Callable from any thread.</summary>
    public void Close(string reason)
    {
        Socket[] handshakes;
        bool started;
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            started = _started;
            handshakes = [.. _handshakes];
            _connectTimer?.Dispose();
        }

        StopListening();
        foreach (var socket in handshakes)
        {
            socket.Dispose();
        }

        if (started)
        {
            _sender.Cancel(reason);
        }
        else
        {
            _logger.Info($"Send {SendId}: direct connection on port {Port} closed, {reason}.");
            Finish(null);
        }
    }

    private static string MegabytesPerSecond(double bytesPerSecond) => (bytesPerSecond / 1_000_000).ToString("0.0", CultureInfo.InvariantCulture);

    private FileStream OpenFile(int index) =>
        new(_files[index].Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

    private void Start()
    {
        var thread = new Thread(Accept)
        {
            IsBackground = true,
            Name = "UltimateRDP send " + SendId,
        };
        thread.Start();
    }

    private void CloseIfNotConnected()
    {
        lock (_lock)
        {
            if (_closed || _started)
            {
                return;
            }
        }

        Close($"connection 0 did not come within {ConnectTimeout.TotalSeconds:0} s of the answer of the client");
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

    /// <summary>Accepts sockets until every connection of the offer came, four more than that were accepted, or the offer closes.</summary>
    private void Accept()
    {
        try
        {
            while (true)
            {
                lock (_lock)
                {
                    if (_closed || _accepted >= MaxConnections + DirectListener.ExtraSockets)
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
                    break;
                }

                int number;
                lock (_lock)
                {
                    if (_closed)
                    {
                        socket.Dispose();
                        break;
                    }

                    _handshakes.Add(socket);
                    number = ++_accepted;
                }

                var thread = new Thread(() => Serve(socket))
                {
                    IsBackground = true,
                    Name = "UltimateRDP send " + SendId + " socket " + number.ToString(CultureInfo.InvariantCulture),
                };
                thread.Start();
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Send {SendId}: unexpected error while accepting direct connections.", ex);
            Close("unexpected error");
        }
        finally
        {
            StopListening();
        }
    }

    /// <summary>One socket: TLS, its token frame of the receiving role, the ready frame, then its part of the transfer.</summary>
    private void Serve(Socket socket)
    {
        var remote = socket.RemoteEndPoint?.ToString() ?? "(unknown address)";
        var keep = false;
        try
        {
            keep = ServeTls(socket, remote);
        }
        catch (Exception ex)
        {
            _logger.Error($"Send {SendId}: unexpected error on a direct connection from {remote}.", ex);
        }
        finally
        {
            lock (_lock)
            {
                _handshakes.Remove(socket);
            }

            if (!keep)
            {
                socket.Dispose();
            }
        }
    }

    /// <summary>True when the socket now belongs to the sender, which closes it.</summary>
    private bool ServeTls(Socket socket, string remote)
    {
        socket.NoDelay = true;
        socket.ReceiveTimeout = (int)HandshakeTimeout.TotalMilliseconds;
        socket.SendTimeout = (int)HandshakeTimeout.TotalMilliseconds;
        using var deadline = new Timer(_ => socket.Dispose(), null, HandshakeTimeout, Timeout.InfiniteTimeSpan);
        var tls = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
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
            _logger.Warn($"Send {SendId}: the TLS handshake of a direct connection from {remote} failed, the connection is dropped. {ex.Message}");
            tls.Dispose();
            return false;
        }

        string? refusal;
        var index = -1;
        try
        {
            var reader = new DirectFrameReader(tls);
            if (!reader.TryRead(out var type, out var payload))
            {
                refusal = "The connection closed before its token.";
                index = -1;
            }
            else
            {
                refusal = type == DirectFrameType.Token ? _gate.Check(payload.Span, out index) : $"The first frame is {type}, not a token.";
                index = refusal is null ? index : -1;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            refusal = "The token frame could not be read: " + ex.Message;
            index = -1;
        }

        if (refusal is not null)
        {
            _logger.Warn($"Send {SendId}: the direct connection from {remote} did not present a token of the offer and is dropped. {refusal}");
            tls.Dispose();
            return false;
        }

        var protocol = string.Create(CultureInfo.InvariantCulture, $"{tls.SslProtocol} ({tls.NegotiatedCipherSuite})");
        deadline.Change(Timeout.Infinite, Timeout.Infinite);
        socket.ReceiveTimeout = (int)IdleTimeout.TotalMilliseconds;
        socket.SendTimeout = (int)IdleTimeout.TotalMilliseconds;
        var connection = new DirectConnection(tls, abort: socket.Dispose);
        try
        {
            DirectFrames.Write(tls, DirectFrameType.Ready, []);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            _logger.Warn($"Send {SendId}: connection {index} from {remote} closed before its ready frame. {ex.Message}");
            connection.Dispose();
            return true;
        }

        if (index != 0)
        {
            _logger.Info($"Send {SendId}: connection {index} from {remote} accepted with {protocol}.");
            if (!_sender.Attach(index, connection))
            {
                _logger.Info($"Send {SendId}: connection {index} came after the end of the transfer or beyond the planned connections; closed.");
            }

            return true;
        }

        lock (_lock)
        {
            if (_closed)
            {
                connection.Dispose();
                return true;
            }

            _started = true;
            _remote = remote;
            _protocol = protocol;
            _connectTimer?.Dispose();
        }

        _logger.Info($"Send {SendId}: connection 0 from {remote} accepted with {protocol}; sending the list of the files.");
        var outcome = _sender.RunAccepted(connection);
        lock (_lock)
        {
            _closed = true;
            _rateTimer?.Dispose();
        }

        StopListening();
        Summarize(outcome);
        Finish(outcome);
        return true;
    }

    private void OnStarted()
    {
        _logger.Info($"Send {SendId}: the client checked the list and created the files; the data goes over up to {_sender.PlannedConnections} connection(s).");
        var timer = new Timer(
            _ =>
            {
                if (_sender.Meter.Sample() is { } rate && _logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug(string.Create(CultureInfo.InvariantCulture, $"Send {SendId}: {MegabytesPerSecond(rate)} MB/s in the last second through the direct connection, {_sender.Meter.Bytes} bytes written, {_sender.AcknowledgedBytes} acknowledged."));
                }
            },
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        lock (_lock)
        {
            _rateTimer = timer;
        }
    }

    private void Finish(DirectSendOutcome? outcome)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        try
        {
            Finished?.Invoke(this, outcome);
        }
        catch (Exception ex)
        {
            _logger.Error($"Send {SendId}: the end handler of the direct connection failed.", ex);
        }
    }

    /// <summary>The end line, then the statistics line, in the words of the client of a drop.</summary>
    private void Summarize(DirectSendOutcome outcome)
    {
        string remote;
        string protocol;
        lock (_lock)
        {
            remote = _remote;
            protocol = _protocol;
        }

        var stats = _sender.Stats();
        var acknowledged = _sender.AcknowledgedBytes;
        var total = _files.Sum(file => file.File.Size);
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"Send {SendId}: {outcome.Status} through the direct connection to {remote} on port {Port}, {protocol}, {_files.Count} file(s), {acknowledged} of {total} bytes acknowledged in {stats.Seconds:0.0} s, {MegabytesPerSecond(stats.Rates.Average)} MB/s; {outcome.Detail}");
        if (outcome.Status is DirectSendStatus.Succeeded or DirectSendStatus.Cancelled or DirectSendStatus.AgentCancelled)
        {
            _logger.Info(line);
        }
        else
        {
            _logger.Warn(line);
        }

        var perConnection = string.Join(", ", stats.BytesPerConnection.Select((bytes, index) => (bytes, index)).Where(pair => pair.bytes >= 0).Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.index}: {pair.bytes}")));
        _logger.Info(string.Create(
            CultureInfo.InvariantCulture,
            $"Send {SendId}: direct connection statistics: {stats.Connections} of {_sender.PlannedConnections} planned connection(s), bytes per connection {perConnection}; {stats.Seconds:0.0} s from the go frame; the reader spent {stats.ReadSeconds:0.00} s opening and reading the files and waited {stats.QueueFullSeconds:0.00} s for room in the queue; the writers spent {stats.WriteSeconds:0.00} s in socket writes and waited {stats.QueueEmptySeconds:0.00} s for blocks, summed over the connections{(Pacer is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $", and {stats.PacedSeconds:0.00} s for the rate limit"))}; average {MegabytesPerSecond(stats.Rates.Average)} MB/s, first 5 s {MegabytesPerSecond(stats.Rates.First)} MB/s, last 5 s {MegabytesPerSecond(stats.Rates.Last)} MB/s."));
    }
}
