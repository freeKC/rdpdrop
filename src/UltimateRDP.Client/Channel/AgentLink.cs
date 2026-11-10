using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AxMSTSCLib;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Rdp;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;
using Message = UltimateRDP.Core.Protocol.Message;

namespace UltimateRDP.Client.Channel;

/// <summary>
/// Runs the UltimateRDP protocol over the virtual channel of the control: agent discovery
/// with hello messages, byte model detection, the live drag messages, the log off request
/// and their answers.
/// </summary>
/// <remarks>
/// <para>
/// Every call and every event happens on the UI thread, like the control itself.
/// </para>
/// <para>
/// How mstscax turns the channel string into bytes is not documented. Every message is
/// sent as the ASCII line itself, one character per byte of text. Whatever the control
/// makes of such a string, it makes the same of every message, and the agent detects
/// that byte model from the first bytes it receives on a connection and keeps it.
/// Sending in two forms would let a late message of the other form reach the agent after
/// it locked its model and spoil the line that follows it.
/// </para>
/// <para>
/// The model detected on the reply of the agent only tells how to read what the control
/// delivers; it is kept for the rest of the connection. The hello of an agent 0.5.0 or
/// later also says the model in which the data of the client reaches it
/// (<see cref="ClientToAgentModel"/>): in the UTF-16 model the chunks of a transfer go out
/// in the dense encoding <c>u15</c>, whose lines are not ASCII; every other line stays
/// ASCII.
/// </para>
/// <para>
/// Every line goes into a <see cref="SendQueue"/> first. The queue is sent at the end of
/// the current message of the UI thread, or at once when more characters wait than one
/// call may hold, in as few calls of the control as that limit allows (the
/// <c>transferBatchCharacters</c> setting, 200 000 characters by default), and each
/// string handed to the control stays allocated until the agent received it
/// (<see cref="OwnedChannelData"/>). A short line is therefore never alone in a call just
/// before a large one, the case where the control lost lines in production. A call that
/// fails empties the queue and raises <see cref="SendFailed"/> with the lines lost.
/// </para>
/// <para>
/// Since 0.5.1, when the queue is about to send less than
/// <see cref="SendQueue.SmallBatchCharacters"/> and <see cref="LargeLineComing"/> says that
/// the transfer thread is about to hand over a chunk, the send waits for that chunk, at
/// most <see cref="ShapingWaitMilliseconds"/>, so that the short lines and the chunk share
/// one call.
/// </para>
/// <para>
/// Every call of the control is timed. During a transfer, between
/// <see cref="BeginTransferMeasure"/> and <see cref="EndTransferMeasure"/>, the calls are
/// summed up (<see cref="ChannelCallStats"/>) and each acknowledgement is matched with the
/// call that carried its last byte (<see cref="AckRoundTrips"/>); the end logs both
/// summaries.
/// </para>
/// </remarks>
internal sealed class AgentLink : IDisposable
{
    /// <summary>Longest wait of short lines for a chunk that is about to be handed over.</summary>
    public const int ShapingWaitMilliseconds = 20;

    private const int HelloIntervalMilliseconds = 2000;
    private const string FileAckPrefix = "{\"type\":\"" + MessageTypes.FileAck + "\"";
    private static readonly TimeSpan DiscoveryPeriod = TimeSpan.FromSeconds(60);

    private readonly RdpControl _control;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = HelloIntervalMilliseconds };
    private readonly System.Windows.Forms.Timer _shapingTimer = new() { Interval = ShapingWaitMilliseconds };
    private readonly ChannelReceiver _receiver = new();
    private readonly SendQueue _queue;
    private readonly OwnedChannelData _data;

    private DateTime _discoveryDeadline;
    private int _hellosSent;
    private bool _connected;
    private bool _flushPosted;
    private long? _shapingDeadline;
    private long _channelCalls;
    private TransferMeasure? _measure;

    /// <param name="control">The Remote Desktop control.</param>
    /// <param name="batchCharacters">Most characters in one call of the control, see <see cref="SendQueue.ClampBatchLimit"/>.</param>
    /// <param name="windowBytes">The window of the transfers, which sets how many sent strings may be kept.</param>
    public AgentLink(RdpControl control, int batchCharacters, long windowBytes = TransferSender.DefaultWindowBytes)
    {
        _control = control;
        _queue = new SendQueue(SendQueue.ClampBatchLimit(batchCharacters));
        _data = new OwnedChannelData(control, SentDataRetention<IntPtr>.LimitForWindow(windowBytes));
        _control.OnChannelReceivedData += OnChannelReceivedData;
        _timer.Tick += (_, _) => OnTimer();
        _shapingTimer.Tick += (_, _) => OnShapingTimeout();
    }

    /// <summary>Raised when <see cref="State"/> changes, and when the agent answers again.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised for every dragAck message.</summary>
    public event EventHandler<DragAckMessage>? DragAckReceived;

    /// <summary>Raised for every dropResult message.</summary>
    public event EventHandler<DropResultMessage>? DropResultReceived;

    /// <summary>Raised for every logoffResult message.</summary>
    public event EventHandler<LogoffResultMessage>? LogoffResultReceived;

    /// <summary>Raised for every fileAck message, without a log line: several come every second during a transfer.</summary>
    public event EventHandler<FileAckMessage>? FileAckReceived;

    /// <summary>Raised for every transferResult message.</summary>
    public event EventHandler<TransferResultMessage>? TransferResultReceived;

    /// <summary>Raised for every transferResend message: the agent asks for lines of a transfer again.</summary>
    public event EventHandler<TransferResendMessage>? TransferResendReceived;

    /// <summary>
    /// Raised when the control refused a call: the lines of that call and every line still
    /// waiting are lost, in their order.
    /// </summary>
    public event EventHandler<IReadOnlyList<QueuedLine>>? SendFailed;

    public AgentState State { get; private set; } = AgentState.NotConnected;

    /// <summary>Version reported by the agent, or null before it answers.</summary>
    public string? AgentVersion { get; private set; }

    /// <summary>Whether the agent sees at least one redirected drive, or null before it answers.</summary>
    public bool? DriveRedirectionAvailable { get; private set; }

    /// <summary>Byte model of the strings received, locked when the agent answered, Unknown before.</summary>
    public ChannelByteModel Model => _receiver.LockedModel;

    /// <summary>
    /// Byte model in which the data of the client reaches the agent, as the hello of the
    /// agent says; Unknown before it answers, and with an agent older than 0.5.0, which
    /// does not say.
    /// </summary>
    public ChannelByteModel ClientToAgentModel { get; private set; }

    /// <summary>
    /// True when the hello of the agent says that it reads numbered transfer lines and asks
    /// for the missing ones, from 0.5.1; the transfers then number their lines.
    /// </summary>
    public bool ReliableTransfer { get; private set; }

    /// <summary>
    /// Says whether the transfer thread is about to hand over a chunk line. Short lines wait
    /// for it, see <see cref="ShapingWaitMilliseconds"/>. Called on the UI thread.
    /// </summary>
    public Func<bool>? LargeLineComing { get; set; }

    /// <summary>
    /// Finds out, once, how strings are handed to the control, and logs it. Called before
    /// the control connects, so that the log says it even when the connection fails.
    /// </summary>
    public void PrepareChannel() => _data.Prepare();

    /// <summary>
    /// Starts agent discovery. Called on OnConnected.
    /// </summary>
    public void Start()
    {
        _connected = true;
        ResetProtocol();

        // A new connection needs a new answer, even if the control raised OnConnected
        // again without OnDisconnected in between.
        State = AgentState.NotConnected;
        RestartDiscovery("connected");
    }

    /// <summary>
    /// Starts a new discovery period of sixty seconds, unless the agent already answered.
    /// Called after logon, because the agent only starts once the user session is open,
    /// and from the menu.
    /// </summary>
    public void RestartDiscovery(string reason)
    {
        if (!_connected || State == AgentState.Ready)
        {
            return;
        }

        _discoveryDeadline = DateTime.UtcNow + DiscoveryPeriod;
        ClientLog.Info($"Agent discovery started ({reason}).");
        SetState(AgentState.Waiting);
        SendHello();
        _timer.Start();
    }

    /// <summary>
    /// The agent did not answer a message that always gets an answer. The control does not
    /// report that the agent exited or restarted in the session, so the link would stay
    /// ready with nobody listening: a new discovery period starts, with a hello that also
    /// gives a restarted agent the language again. Does nothing unless the agent is ready.
    /// </summary>
    public void Recheck(string reason)
    {
        if (!_connected || State != AgentState.Ready)
        {
            return;
        }

        ClientLog.Info($"The agent did not answer ({reason}); it may have exited or restarted, looking for it again.");
        SetState(AgentState.Waiting);
        RestartDiscovery(reason);
    }

    /// <summary>
    /// Stops everything. Called on OnDisconnected.
    /// </summary>
    public void Stop()
    {
        _connected = false;
        _timer.Stop();
        ResetProtocol();
        SetState(AgentState.NotConnected);
    }

    /// <summary>
    /// Sends a live drag message (dragEnter, dragMove, drop or dragLeave). Moves are not
    /// logged one by one at the info level: about thirty go out every second. Returns false
    /// when the agent is not ready or the message cannot be encoded; a refusal of the
    /// control comes later, through <see cref="SendFailed"/>.
    /// </summary>
    public bool SendDrag(Message message, string id)
    {
        if (State != AgentState.Ready || Model == ChannelByteModel.Unknown)
        {
            return false;
        }

        return Send(message, id, $"{message.Type} {id}", log: message is not DragMoveMessage);
    }

    /// <summary>
    /// Sends the log off request. Returns false when the agent is not ready or the message
    /// cannot be encoded.
    /// </summary>
    public bool SendLogoff(LogoffMessage message)
    {
        if (State != AgentState.Ready || Model == ChannelByteModel.Unknown)
        {
            return false;
        }

        return Send(message, message.Id, $"logoff {message.Id}");
    }

    /// <summary>
    /// Sends a message of a transfer. transferBegin and transferCancel are logged at the
    /// info level, the messages of each file at the debug level, since a folder may hold
    /// thousands of files. Returns false when the agent is not ready or the message cannot
    /// be encoded; a refusal of the control comes later, through <see cref="SendFailed"/>.
    /// </summary>
    public bool SendTransfer(Message message, string transferId)
    {
        if (State != AgentState.Ready || Model == ChannelByteModel.Unknown)
        {
            return false;
        }

        return Send(message, transferId, Describe(message, transferId), log: message is TransferBeginMessage or TransferCancelMessage);
    }

    /// <summary>
    /// Queues ready lines of a transfer, in their order, during the same UI message so that
    /// they leave in the same call when they fit: the short lines that come before a chunk
    /// travel with it. transferBegin is logged at the info level, the other lines at the
    /// debug level, and lines sent again are counted by the caller. Returns false when the
    /// agent is not ready; a refusal of the control comes later, through <see cref="SendFailed"/>.
    /// </summary>
    public bool SendTransferLines(IReadOnlyList<QueuedLine> lines)
    {
        if (State != AgentState.Ready || Model == ChannelByteModel.Unknown)
        {
            return false;
        }

        foreach (var line in lines)
        {
            var what = line.Label ?? $"{line.Type} {line.Subject}";
            var seq = line.Seq >= 0 ? Invariant($" line {line.Seq}") : string.Empty;
            var again = line.IsResend ? "Sent again" : "Sent";
            if (line.Type == MessageTypes.TransferBegin && !line.IsResend)
            {
                ClientLog.Info(Invariant($"{again} {what}{seq}, {line.Text.Length} characters."));
            }
            else if (ClientLog.DebugEnabled)
            {
                var position = line.IsChunk ? Invariant($", {line.TransferPosition} bytes of the transfer") : string.Empty;
                ClientLog.Debug(Invariant($"{again} {what}{seq}{position}, {line.Text.Length} characters."));
            }

            _queue.Enqueue(line);
        }

        SendFullBatchesAndScheduleFlush();
        return true;
    }

    /// <summary>
    /// The agent acknowledged the first <paramref name="acknowledgedBytes"/> of a transfer:
    /// the strings of the calls that it surely received in full are freed.
    /// </summary>
    public void OnTransferAcknowledged(string transferId, long acknowledgedBytes)
    {
        _data.OnAcknowledged(transferId, acknowledgedBytes);
        if (_measure is not { } measure || measure.TransferId != transferId)
        {
            return;
        }

        if (measure.Acks.OnAck(acknowledgedBytes, Stopwatch.GetTimestamp()) is { } sample && ClientLog.DebugEnabled)
        {
            ClientLog.Debug(Invariant($"Transfer {transferId}: acknowledgement of {sample.AcknowledgedBytes} bytes, {Milliseconds(sample.RoundTripTicks):0} ms after the call that carried them returned, {sample.InFlightBytes} bytes handed to the control and not acknowledged."));
        }
    }

    /// <summary>
    /// Starts measuring the calls of the control and the acknowledgements for the transfer
    /// <paramref name="transferId"/>, whose window is <paramref name="windowBytes"/>.
    /// </summary>
    public void BeginTransferMeasure(string transferId, long windowBytes) =>
        _measure = new TransferMeasure(transferId, windowBytes, Stopwatch.GetTimestamp());

    /// <summary>Logs the summaries of the calls and of the acknowledgements of the transfer, and stops measuring.</summary>
    public void EndTransferMeasure(string transferId)
    {
        if (_measure is not { } measure || measure.TransferId != transferId)
        {
            return;
        }

        _measure = null;
        var elapsed = Stopwatch.GetElapsedTime(measure.StartTicks).TotalMilliseconds;
        ClientLog.Info(measure.Calls.Describe(transferId, Stopwatch.Frequency, elapsed));
        ClientLog.Info(measure.Acks.Describe(transferId, Stopwatch.Frequency, measure.WindowBytes));
    }

    /// <summary>
    /// Sends a hello with the current user interface language, after the user changed it,
    /// so that the agent writes its next texts in that language. The agent answers with a
    /// hello, which keeps the link ready. Does nothing unless the agent is ready.
    /// </summary>
    public void AnnounceLanguage()
    {
        if (!_connected || State != AgentState.Ready)
        {
            return;
        }

        ClientLog.Info($"Telling the agent the new language, {UiLanguage.CurrentCode}.");
        SendHello();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _shapingTimer.Dispose();
        _control.OnChannelReceivedData -= OnChannelReceivedData;
        _queue.Clear();
        _data.Dispose();
    }

    private void ResetProtocol()
    {
        StopShapingWait();
        var dropped = _queue.Clear();
        if (dropped.Count > 0)
        {
            ClientLog.Info(Invariant($"{dropped.Count} line(s) waiting to be sent were dropped: the connection ended. The first one: {dropped[0].Type} {dropped[0].Subject}."));
        }

        _receiver.Reset();
        _hellosSent = 0;
        AgentVersion = null;
        DriveRedirectionAvailable = null;
        ClientToAgentModel = ChannelByteModel.Unknown;
        ReliableTransfer = false;
    }

    private void OnTimer()
    {
        if (!_connected || State == AgentState.Ready)
        {
            _timer.Stop();
            return;
        }

        if (DateTime.UtcNow >= _discoveryDeadline)
        {
            _timer.Stop();
            ClientLog.Warn(Invariant($"No answer from the agent after {_hellosSent} hello message(s)."));
            SetState(AgentState.NotFound);
            return;
        }

        SendHello();
    }

    private void SendHello()
    {
        _hellosSent++;
        var what = Invariant($"hello #{_hellosSent}");
        Send(HelloMessage.FromClient(AppInfo.Version, UiLanguage.CurrentCode), what, what);
    }

    /// <summary>Encodes the message and queues its line. False when it cannot be encoded.</summary>
    private bool Send(Message message, string subject, string what, bool log = true)
    {
        string line;
        try
        {
            line = LineFraming.EncodeLine(message);
        }
        catch (ArgumentException ex)
        {
            ClientLog.Error($"Cannot encode {what}.", ex);
            return false;
        }

        // The line leaves with the queue, at the end of the current UI message at the latest.
        if (log)
        {
            ClientLog.Info(Invariant($"Sent {what}, {line.Length} characters."));
        }
        else if (ClientLog.DebugEnabled)
        {
            ClientLog.Debug(Invariant($"Sent {what}, {line.Length} characters."));
        }

        Enqueue(new QueuedLine(line, message.Type, subject));
        return true;
    }

    /// <summary>
    /// Queues a line. When more characters wait than one call may hold, full calls go out at
    /// once; the rest goes out at the end of the current UI message.
    /// </summary>
    private void Enqueue(QueuedLine line)
    {
        _queue.Enqueue(line);
        SendFullBatchesAndScheduleFlush();
    }

    /// <summary>
    /// Sends full calls at once while more characters wait than one call may hold, and posts
    /// a flush for the rest at the end of the current UI message.
    /// </summary>
    private void SendFullBatchesAndScheduleFlush()
    {
        while (_queue.IsOverLimit)
        {
            if (!SendBatch(_queue.TakeBatch()!))
            {
                return;
            }
        }

        if (_queue.Count == 0 || _flushPosted)
        {
            return;
        }

        if (!_control.IsHandleCreated)
        {
            // Nothing would run a posted flush without a handle; the control reports the failure.
            Flush();
            return;
        }

        _flushPosted = true;
        _control.BeginInvoke(Flush);
    }

    /// <summary>
    /// Sends every line waiting, in calls of at most the limit of the queue, unless short
    /// lines wait for a chunk that is about to come.
    /// </summary>
    private void Flush()
    {
        _flushPosted = false;
        if (MustWaitForLargeLine())
        {
            // The chunk queues itself and posts a new flush; the timer sends without it.
            return;
        }

        StopShapingWait();
        while (_queue.TakeBatch() is { } batch)
        {
            if (!SendBatch(batch))
            {
                return;
            }
        }
    }

    /// <summary>
    /// True when the lines waiting are short, a chunk is about to come, and the wait of
    /// <see cref="ShapingWaitMilliseconds"/> is not over. Starts the wait the first time.
    /// </summary>
    private bool MustWaitForLargeLine()
    {
        if (_queue.Count == 0 || _queue.QueuedCharacters >= SendQueue.SmallBatchCharacters || LargeLineComing?.Invoke() != true)
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (_shapingDeadline is null)
        {
            _shapingDeadline = now + ShapingWaitMilliseconds;
            _shapingTimer.Start();
            if (ClientLog.DebugEnabled)
            {
                ClientLog.Debug(Invariant($"{_queue.Count} short line(s) of {_queue.QueuedCharacters} characters wait for the chunk that is about to come, at most {ShapingWaitMilliseconds} ms."));
            }

            return true;
        }

        return now < _shapingDeadline;
    }

    /// <summary>The wait for a chunk is over: the short lines go without it.</summary>
    private void OnShapingTimeout()
    {
        _shapingTimer.Stop();
        if (_shapingDeadline is null)
        {
            return;
        }

        // Whatever the clock says, the wait is over.
        _shapingDeadline = long.MinValue;
        if (_queue.Count > 0 && ClientLog.DebugEnabled)
        {
            ClientLog.Debug(Invariant($"No chunk came within {ShapingWaitMilliseconds} ms; {_queue.Count} short line(s) go alone."));
        }

        Flush();
    }

    private void StopShapingWait()
    {
        _shapingDeadline = null;
        _shapingTimer.Stop();
    }

    /// <summary>One call of the control. On a failure, the queue is emptied and <see cref="SendFailed"/> raised.</summary>
    private bool SendBatch(SendBatch batch)
    {
        var first = batch.Lines[0];
        try
        {
            // Chunks sent again prove nothing through their acknowledgement: kept for the long time.
            var ticks = _data.Send(ProtocolConstants.ChannelName, batch.Text, batch.FirstNewChunk, holdLong: batch.FirstChunk is not null);
            var end = Stopwatch.GetTimestamp();
            _channelCalls++;
            if (_measure is { } measure)
            {
                measure.Calls.Record(batch.Text.Length, end - ticks, end);
                foreach (var line in batch.Lines)
                {
                    if (line.IsChunk && !line.IsResend && line.Subject == measure.TransferId)
                    {
                        measure.Acks.OnSent(line.TransferPosition, end);
                    }
                }
            }

            if (ClientLog.DebugEnabled)
            {
                ClientLog.Debug(Invariant($"Channel call {_channelCalls}: {batch.Lines.Count} line(s), {batch.Text.Length} characters, {Milliseconds(ticks):0.00} ms in the call, first {first.Type} {first.Subject}; {_data.RetainedCount} string(s) of {_data.RetainedBytes} bytes kept."));
            }

            return true;
        }
        catch (Exception ex) when (ex is COMException or AxHost.InvalidActiveXStateException or InvalidComObjectException)
        {
            ClientLog.Error(Invariant($"SendOnVirtualChannel failed for {batch.Lines.Count} line(s), {batch.Text.Length} characters, first {first.Type} {first.Subject}."), ex);
            List<QueuedLine> lost = [.. batch.Lines, .. _queue.Clear()];
            SendFailed?.Invoke(this, lost);
            return false;
        }
    }

    /// <summary>The message and what it is about, for the log: the file of fileBegin and fileEnd.</summary>
    private static string Describe(Message message, string transferId) => message switch
    {
        FileBeginMessage file => Invariant($"{message.Type} {transferId} file {file.FileIndex}, {file.Size} bytes"),
        FileEndMessage end => Invariant($"{message.Type} {transferId} file {end.FileIndex}"),
        _ => $"{message.Type} {transferId}",
    };

    private void OnChannelReceivedData(object? sender, IMsTscAxEvents_OnChannelReceivedDataEvent e)
    {
        if (!string.Equals(e.chanName, ProtocolConstants.ChannelName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var data = e.data ?? string.Empty;
        var lines = _receiver.Receive(data);

        // The acknowledgements of a transfer come several times a second; they are not logged.
        if (lines.Count == 0 || !lines.TrueForAll(line => line.Text.StartsWith(FileAckPrefix, StringComparison.Ordinal)))
        {
            ClientLog.Info(Invariant($"Received {data.Length} string units on {e.chanName}, first: {FirstUnitsHex(data)}"));
        }

        foreach (var line in lines)
        {
            HandleLine(line);
        }
    }

    private void HandleLine(ReceivedLine line)
    {
        var message = MessageSerializer.Deserialize(Encoding.ASCII.GetBytes(line.Text));
        switch (message)
        {
            case HelloMessage hello when hello.Version > ProtocolConstants.ProtocolVersion:
                ClientLog.Warn(Invariant($"The agent speaks protocol version {hello.Version}; this client speaks {ProtocolConstants.ProtocolVersion}. Ignored."));
                break;

            case HelloMessage hello:
                OnAgentHello(hello, line.Model);
                break;

            case DropResultMessage result:
                ClientLog.Info($"Drop result {result.Id}: {result.Status}, effect {result.Effect?.ToString() ?? "(not said)"}, window {result.TargetWindow?.ClassName ?? "(none)"} \"{result.TargetWindow?.Title}\" {result.TargetWindow?.ProcessName}, message \"{result.Message}\"");
                DropResultReceived?.Invoke(this, result);
                break;

            case DragAckMessage ack:
                ClientLog.Info($"Drag ack {ack.Id}: accepted {ack.Accepted}{(ack.Transfer == true ? ", transfer " + ack.TransferId : string.Empty)}, message \"{ack.Message}\"");
                DragAckReceived?.Invoke(this, ack);
                break;

            case LogoffResultMessage logoff:
                ClientLog.Info($"Log off result {logoff.Id}: accepted {logoff.Accepted}, message \"{logoff.Message}\"");
                LogoffResultReceived?.Invoke(this, logoff);
                break;

            case FileAckMessage fileAck:
                FileAckReceived?.Invoke(this, fileAck);
                break;

            case TransferResultMessage transfer:
                ClientLog.Info(Invariant($"Transfer result {transfer.TransferId}: {transfer.Status}, {transfer.Bytes} bytes in {transfer.Seconds:0.0} s, message \"{transfer.Message}\""));
                TransferResultReceived?.Invoke(this, transfer);
                break;

            case TransferResendMessage resend:
                TransferResendReceived?.Invoke(this, resend);
                break;

            case null:
                ClientLog.Warn(Invariant($"Protocol error: a {line.Text.Length} character line is not a valid message. Ignored."));
                break;

            default:
                ClientLog.Warn($"Unexpected \"{message.Type}\" message from the agent. Ignored.");
                break;
        }
    }

    private void OnAgentHello(HelloMessage hello, ChannelByteModel model)
    {
        if (!_connected)
        {
            ClientLog.Info("Hello from the agent after the disconnection. Ignored.");
            return;
        }

        if (Model == ChannelByteModel.Unknown)
        {
            _receiver.LockedModel = model;
            ClientLog.Info($"Channel byte model locked: {model}.");
        }

        AgentVersion = hello.AgentVersion;
        DriveRedirectionAvailable = hello.DriveRedirectionAvailable;
        ClientToAgentModel = ChannelByteModelNames.FromName(hello.ClientToAgentModel);
        ReliableTransfer = hello.ReliableTransfer == true;
        _timer.Stop();
        ClientLog.Info($"Agent ready, version {hello.AgentVersion ?? "(unknown)"}, drive redirection available: {hello.DriveRedirectionAvailable?.ToString() ?? "(not said)"}, data of the client reaches the agent in the {(hello.ClientToAgentModel is null ? "(not said)" : ClientToAgentModel)} model, so transfer chunks go in {ChunkEncodings.Choose(ClientToAgentModel)}; transfer lines {(ReliableTransfer ? "numbered, the agent asks for the missing ones" : "not numbered")}.");
        State = AgentState.Ready;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetState(AgentState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        ClientLog.Info($"Agent state: {state}.");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string FirstUnitsHex(string data)
    {
        var count = Math.Min(16, data.Length);
        var builder = new StringBuilder(count * 5);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(((int)data[i]).ToString("X4", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>What is measured for one transfer.</summary>
    private sealed record TransferMeasure(string TransferId, long WindowBytes, long StartTicks)
    {
        public ChannelCallStats Calls { get; } = new();

        public AckRoundTrips Acks { get; } = new();
    }
}
