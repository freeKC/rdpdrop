using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Agent;
using UltimateRDP.Core.Direct;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;
using Timer = System.Threading.Timer;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// Receives the files that the session sends, since 0.9.0, through the direct connection of
/// the sendOffer: the client connects as for a drop, connection 0 to the host of the session
/// then to the address hint, TLS pinned by the SHA-256 of the offer, but its token frames say
/// that it receives, and a <see cref="DirectReceiver"/> runs on its side. The files go to the
/// hidden part folder <c>.ultimaterdp-&lt;id&gt;.part</c> of the folder for received files, and
/// move into that folder once every file was checked, under names that replace nothing.
/// </summary>
/// <remarks>
/// A background thread opens the connections; the receiver reads each one on a thread of its
/// own. The events are raised on the UI thread. When connection 0 cannot be made, its TLS
/// handshake fails or its token is refused, <see cref="NotAvailable"/> says why: there is no
/// slower path for this direction.
/// </remarks>
internal sealed class DirectDownload
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly Control _owner;
    private readonly SendOfferMessage _offer;
    private readonly string _host;
    private readonly string _folder;
    private readonly int _connections;
    private readonly DirectReceiver _receiver;
    private readonly FolderDirectStorage _storage;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly List<Socket> _sockets = [];
    private Timer? _rateTimer;
    private string? _reached;
    private volatile bool _stopped;
    private int _zoneFailures;

    /// <param name="owner">A control of the UI thread, to raise the events on.</param>
    /// <param name="offer">The sendOffer, its direct connection well formed.</param>
    /// <param name="host">The host of the session, from the full address of the .rdp file, without the port.</param>
    /// <param name="folder">The folder for received files; it exists.</param>
    /// <param name="connections">The <c>directConnections</c> setting of the client.</param>
    public DirectDownload(Control owner, SendOfferMessage offer, string host, string folder, int connections)
    {
        _owner = owner;
        _offer = offer;
        _host = host;
        _folder = folder;
        var offered = offer.Direct.Connections ?? 1;
        _connections = DirectTransferPolicy.ConnectionsFor(offered, connections, offer.Bytes);
        PartFolder = Path.Combine(folder, ReceiveNames.PartFolderName(offer.Id) ?? throw new ArgumentException("The id of the send is not one the agent sends.", nameof(offer)));
        Directory.CreateDirectory(PartFolder);
        File.SetAttributes(PartFolder, File.GetAttributes(PartFolder) | FileAttributes.Hidden);
        _storage = new FolderDirectStorage(PartFolder, MarkFromTheWeb, reason => ClientLog.Warn($"Receive {SendId}: the part folder {PartFolder} cannot be deleted. {reason}"));
        var token = Convert.FromHexString(offer.Direct.Token);
        _receiver = new DirectReceiver(token, offered, offer.Items, _storage, () => Environment.TickCount64, Describe, HasRoomFor);
        CryptographicOperations.ZeroMemory(token);
        _receiver.Started += () => ClientLog.Info($"Receive {SendId}: the list of {_receiver.ExpectedFiles} file(s), {_receiver.ExpectedBytes} bytes, was checked and the files created; the data comes over up to {_connections} connection(s).");
        _receiver.ConnectionDropped += (index, detail) => ClientLog.Info($"Receive {SendId}: connection {index} closed before it carried any block ({detail}); the transfer goes on without it.");
        _receiver.Ended += OnEnded;
        Progress = new TransferProgress(offer.Bytes, Environment.TickCount64);
    }

    /// <summary>Raised when the direct connection cannot be used; the text says why, in English, and nothing came.</summary>
    public event Action<DirectDownload, string>? NotAvailable;

    /// <summary>Raised when every file is in the folder for received files, with their full paths.</summary>
    public event Action<DirectDownload, IReadOnlyList<string>>? Completed;

    /// <summary>Raised when the transfer failed or was cancelled by the session.</summary>
    public event Action<DirectDownload, LocalText>? Failed;

    public string SendId => _offer.Id;

    public SendOfferMessage Offer => _offer;

    /// <summary>The hidden folder the files arrive in.</summary>
    public string PartFolder { get; }

    /// <summary>The progress, from the bytes received and written.</summary>
    public TransferProgress Progress { get; }

    /// <summary>The address connection 0 reached, as "host:port", once connected.</summary>
    public string? Endpoint { get; private set; }

    /// <summary>The TLS protocol and cipher suite of connection 0.</summary>
    public string? Tls { get; private set; }

    /// <summary>Bytes received and written so far.</summary>
    public long ReceivedBytes => _receiver.ReceivedBytes;

    /// <summary>The files of the list, once connection 0 sent it.</summary>
    public IReadOnlyList<TransferFile> Files => _receiver.ListedFiles;

    public long StartTicks { get; } = Environment.TickCount64;

    /// <summary>Starts the background thread.</summary>
    public void Start()
    {
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "UltimateRDP receive " + SendId,
        };
        thread.Start();
    }

    /// <summary>The user cancelled, or the tab closes: the session gets the final frame, what came is deleted. Raises nothing.</summary>
    public void Cancel(string reason)
    {
        _stopped = true;
        _stop.Cancel();
        _receiver.Cancel(reason);
        CloseSockets();
    }

    /// <summary>The session gave up the send through the channel: the transfer stops and what came is deleted. Raises nothing.</summary>
    public void Stop(string reason)
    {
        _stopped = true;
        _stop.Cancel();
        _receiver.Close(reason);
        CloseSockets();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string MegabytesPerSecond(double bytesPerSecond) => Invariant($"{bytesPerSecond / 1_000_000:0.0}");

    private void Run()
    {
        try
        {
            if (Open(0, out var reason) is not { } control)
            {
                if (!_stopped)
                {
                    ClientLog.Info($"Receive {SendId}: the direct connection cannot be used, {reason}.");
                    _receiver.Close("the direct connection cannot be used");
                    Post(() => NotAvailable?.Invoke(this, reason));
                }

                return;
            }

            ClientLog.Info($"Receive {SendId}: connection 0 to {Endpoint} with {Tls}; the agent accepted the token.");
            if (_receiver.Register(0, control) is { } refusal)
            {
                ClientLog.Info($"Receive {SendId}: connection 0 cannot be used: {refusal}");
                control.Dispose();
                return;
            }

            StartReading(0);
            StartRateLog();
            for (var index = 1; index < _connections && !_stopped; index++)
            {
                var number = index;
                new Thread(() => OpenExtra(number)) { IsBackground = true, Name = "UltimateRDP receive " + SendId + " open " + number.ToString(CultureInfo.InvariantCulture) }.Start();
            }
        }
        catch (Exception ex)
        {
            ClientLog.Error($"Receive {SendId}: unexpected error on the direct connection.", ex);
            _receiver.Close("unexpected error");
        }
    }

    /// <summary>Connection <paramref name="index"/> above 0, to the address connection 0 reached; a failure is logged and is not an error.</summary>
    private void OpenExtra(int index)
    {
        try
        {
            if (Open(index, out var reason) is not { } stream)
            {
                if (!_stopped)
                {
                    ClientLog.Info($"Receive {SendId}: connection {index} is not used: {reason}");
                }

                return;
            }

            if (_receiver.Register(index, stream) is { } refusal)
            {
                ClientLog.Info($"Receive {SendId}: connection {index} is not used: {refusal}");
                stream.Dispose();
                return;
            }

            StartReading(index);
        }
        catch (Exception ex)
        {
            ClientLog.Error($"Receive {SendId}: unexpected error on direct connection {index}.", ex);
        }
    }

    private void StartReading(int index)
    {
        var thread = new Thread(() =>
        {
            _receiver.RunRegistered(index);
        })
        {
            IsBackground = true,
            Name = "UltimateRDP receive " + SendId + " connection " + index.ToString(CultureInfo.InvariantCulture),
        };
        thread.Start();
    }

    /// <summary>
    /// One connection: TCP, TLS pinned, the token frame of the receiving role, the ready frame.
    /// Null with the reason when it cannot be made. Connection 0 tries the host, then the hint.
    /// </summary>
    private SslStream? Open(int index, out string reason)
    {
        var hosts = new List<string>(2);
        if (index > 0)
        {
            if (_reached is not { } reached)
            {
                reason = "connection 0 reached no address";
                return null;
            }

            hosts.Add(reached);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(_host))
            {
                hosts.Add(_host);
            }

            if (!string.IsNullOrWhiteSpace(_offer.Direct.Host) && !string.Equals(_offer.Direct.Host, _host, StringComparison.OrdinalIgnoreCase))
            {
                hosts.Add(_offer.Direct.Host);
            }
        }

        var failures = new List<string>(2);
        foreach (var host in hosts)
        {
            Socket socket;
            try
            {
                socket = ConnectTo(host);
            }
            catch (IOException ex)
            {
                failures.Add(ex.Message);
                continue;
            }

            var endpoint = host + ":" + _offer.Direct.Port.ToString(CultureInfo.InvariantCulture);
            lock (_gate)
            {
                _sockets.Add(socket);
            }

            var tls = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false, (_, certificate, _, _) => IsPinned(certificate, endpoint));
            try
            {
                tls.AuthenticateAsClient(new SslClientAuthenticationOptions
                {
                    TargetHost = "UltimateRDP agent",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    EncryptionPolicy = EncryptionPolicy.RequireEncryption,
                });
                var token = Convert.FromHexString(_offer.Direct.Token);
                DirectFrames.Write(tls, DirectFrameType.Token, DirectFrames.TokenPayload(token, (byte)index, DirectRole.ClientReceives));
                CryptographicOperations.ZeroMemory(token);
                if (!new DirectFrameReader(tls).TryRead(out var type, out _) || type != DirectFrameType.Ready)
                {
                    tls.Dispose();
                    failures.Add($"the agent did not accept the token on {endpoint}");
                    continue;
                }

                socket.ReceiveTimeout = (int)IdleTimeout.TotalMilliseconds;
                socket.SendTimeout = (int)IdleTimeout.TotalMilliseconds;
                if (index == 0)
                {
                    _reached = host;
                    Endpoint = endpoint;
                    Tls = Invariant($"{tls.SslProtocol} ({tls.NegotiatedCipherSuite})");
                }

                reason = string.Empty;
                return tls;
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or ObjectDisposedException or SocketException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                tls.Dispose();
                failures.Add($"the TLS handshake or the token with {endpoint} failed ({ex.Message})");
            }
        }

        reason = failures.Count == 0 ? "no address to connect to" : "cannot connect, " + string.Join("; ", failures);
        return null;
    }

    private Socket ConnectTo(string host)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(DirectUpload.ConnectTimeout);
        try
        {
            socket.ConnectAsync(host, _offer.Direct.Port, timeout.Token).AsTask().GetAwaiter().GetResult();
            socket.ReceiveTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            socket.SendTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            return socket;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            socket.Dispose();
            throw new IOException(ex is OperationCanceledException
                ? Invariant($"{host}:{_offer.Direct.Port} did not answer within {DirectUpload.ConnectTimeout.TotalSeconds:0} s")
                : Invariant($"{host}:{_offer.Direct.Port} refused or unreachable ({ex.Message})"));
        }
    }

    private bool IsPinned(X509Certificate? certificate, string endpoint)
    {
        if (certificate is null)
        {
            return false;
        }

        var hash = TransferHash.ToHex(certificate.GetCertHash(HashAlgorithmName.SHA256));
        if (string.Equals(hash, _offer.Direct.CertSha256, StringComparison.Ordinal))
        {
            return true;
        }

        ClientLog.Warn($"Receive {SendId}: the certificate of {endpoint} has the SHA-256 {hash}, not the one the agent gave through the channel; the direct connection is refused.");
        return false;
    }

    private void CloseSockets()
    {
        Socket[] sockets;
        lock (_gate)
        {
            sockets = [.. _sockets];
        }

        foreach (var socket in sockets)
        {
            socket.Dispose();
        }
    }

    private void StartRateLog()
    {
        var timer = new Timer(
            _ =>
            {
                if (_receiver.Meter.Sample() is { } rate && ClientLog.DebugEnabled)
                {
                    ClientLog.Debug(Invariant($"Receive {SendId}: {MegabytesPerSecond(rate)} MB/s in the last second through the direct connection, {_receiver.ReceivedBytes} of {_receiver.ExpectedBytes} bytes over {_receiver.Connections} connection(s)."));
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

    /// <summary>Every received file gets the Mark of the Web, as a download of a browser.</summary>
    private void MarkFromTheWeb(string path)
    {
        try
        {
            File.WriteAllText(path + ":" + ReceivePolicy.ZoneStream, ReceivePolicy.ZoneIdentifier());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (Interlocked.Increment(ref _zoneFailures) == 1)
            {
                ClientLog.Warn($"Receive {SendId}: the Mark of the Web cannot be written on {path}, as on a drive without alternate data streams; the files are kept without it. {ex.Message}");
            }
        }
    }

    private bool HasRoomFor(long bytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_folder));
            if (string.IsNullOrEmpty(root))
            {
                return true;
            }

            var free = new DriveInfo(root).AvailableFreeSpace;
            var required = FastTransferPolicy.RequiredFreeSpace(bytes);
            if (free < required)
            {
                ClientLog.Warn($"Receive {SendId}: {bytes} bytes announced, {required} bytes of free space needed in {_folder} and {free} free. Refused.");
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            ClientLog.Info($"Receive {SendId}: the free space of {_folder} is not known. {ex.Message}");
        }

        return true;
    }

    /// <summary>The text of the final frame, in English: the agent only logs it.</summary>
    private static string? Describe(DirectReceiver receiver) => receiver.State switch
    {
        DirectReceiverState.Succeeded => null,
        DirectReceiverState.Cancelled => "The user of the client cancelled the transfer.",
        _ => $"The client could not receive the files: {receiver.Failure}{(receiver.FailurePath is { } path ? " (" + path + ")" : string.Empty)}.",
    };

    /// <summary>The transfer is over and nothing touches its files: move them in, or say what failed. On a thread of the receiver.</summary>
    private void OnEnded()
    {
        lock (_gate)
        {
            _rateTimer?.Dispose();
        }

        LogStatistics();
        if (_stopped && _receiver.State != DirectReceiverState.Succeeded)
        {
            return;
        }

        switch (_receiver.State)
        {
            case DirectReceiverState.Succeeded:
                try
                {
                    var moved = ReceivePolicy.MoveIn(PartFolder, _folder, _offer.Items);
                    TryDeletePartFolder();
                    ClientLog.Info($"Receive {SendId}: {moved.Count} item(s) moved into {_folder}: {string.Join(", ", moved.Select(Path.GetFileName))}.");
                    Post(() => Completed?.Invoke(this, moved));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ClientLog.Warn($"Receive {SendId}: the files were received but cannot be moved into {_folder}; they stay in {PartFolder}. {ex.Message}");
                    var message = ex.Message;
                    Post(() => Failed?.Invoke(this, LocalText.Of(nameof(Strings.ReceiveMoveFailed), _folder, message, PartFolder)));
                }

                break;

            case DirectReceiverState.Cancelled:
                ClientLog.Info($"Receive {SendId}: cancelled by the session, {_receiver.CancelReason}; what came is deleted.");
                var reason = _receiver.CancelReason ?? string.Empty;
                Post(() => Failed?.Invoke(this, LocalText.Of(nameof(Strings.ReceiveCancelledBySession), reason)));
                break;

            default:
                ClientLog.Warn($"Receive {SendId}: failed, {_receiver.Failure}: {_receiver.FailureDetail} What came is deleted.");
                var path = _receiver.FailurePath ?? string.Empty;
                var detail = _receiver.FailureDetail ?? string.Empty;
                var text = _receiver.Failure switch
                {
                    DirectFailure.NoSpace => LocalText.Of(nameof(Strings.ReceiveRefusedNoSpace), _folder, FastTransferPolicy.RequiredFreeSpace(_receiver.ExpectedBytes) / 1_000_000.0, FreeMegabytes()),
                    DirectFailure.HashMismatch or DirectFailure.SizeMismatch => LocalText.Of(nameof(Strings.ReceiveChecksum), path),
                    DirectFailure.BadName => LocalText.Of(nameof(Strings.ReceiveBadName), path),
                    DirectFailure.WriteFailed => LocalText.Of(nameof(Strings.ReceiveWriteFailed), path, detail),
                    _ => LocalText.Of(nameof(Strings.ReceiveLost)),
                };
                Post(() => Failed?.Invoke(this, text));
                break;
        }
    }

    private double FreeMegabytes()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_folder))!).AvailableFreeSpace / 1_000_000.0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void TryDeletePartFolder()
    {
        try
        {
            Directory.Delete(PartFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ClientLog.Warn($"Receive {SendId}: the empty part folder {PartFolder} cannot be deleted. {ex.Message}");
        }
    }

    /// <summary>The two lines of the end, in the words of the agent of a drop: the outcome, then where the time went.</summary>
    private void LogStatistics()
    {
        var receiver = _receiver;
        var seconds = receiver.Milliseconds / 1000.0;
        var rate = seconds > 0 ? receiver.ReceivedBytes / seconds : 0;
        ClientLog.Info(Invariant(
            $"Receive {SendId}: {receiver.State} through the direct connection to {Endpoint ?? "(not connected)"}, {Tls ?? "no TLS session"}, {receiver.ReceivedFiles} of {receiver.ExpectedFiles} file(s), {receiver.ReceivedBytes} of {receiver.ExpectedBytes} bytes in {seconds:0.0} s, {MegabytesPerSecond(rate)} MB/s, {receiver.AcksSent} acknowledgement(s){(receiver.State == DirectReceiverState.Failed ? $", {receiver.Failure}: {receiver.FailureDetail}" : string.Empty)}."));
        if (!receiver.HasControl)
        {
            return;
        }

        var rates = receiver.Meter.Rates();
        var perConnection = string.Join(", ", receiver.BytesPerConnection.Select((bytes, index) => (bytes, index)).Where(pair => pair.bytes >= 0).Select(pair => Invariant($"{pair.index}: {pair.bytes}")));
        ClientLog.Info(Invariant(
            $"Receive {SendId}: direct connection statistics: {receiver.Connections} connection(s), bytes per connection {perConnection}; socket reads {receiver.ReadSeconds:0.00} s, file writes {receiver.WriteSeconds:0.00} s, hashing {receiver.HashSeconds:0.00} s, summed over the threads; average {MegabytesPerSecond(rates.Average)} MB/s, first 5 s {MegabytesPerSecond(rates.First)} MB/s, last 5 s {MegabytesPerSecond(rates.Last)} MB/s."));
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread, after the actions posted before.</summary>
    private void Post(Action action)
    {
        try
        {
            _owner.BeginInvoke(action);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The session view is gone: nothing is left to report to.
        }
    }
}
