using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AxMSTSCLib;
using RdpDrop.Client.Logging;
using RdpDrop.Client.Rdp;
using RdpDrop.Core.Protocol;
using Message = RdpDrop.Core.Protocol.Message;

namespace RdpDrop.Client.Channel;

/// <summary>
/// Runs the RdpDrop protocol over the virtual channel of the control: agent discovery
/// with hello messages, byte model detection, drop messages and their results.
/// </summary>
/// <remarks>
/// <para>
/// Every call and every event happens on the UI thread, like the control itself.
/// </para>
/// <para>
/// How mstscax turns the channel string into bytes is not documented. Until the agent
/// answers, hello messages alternate between the two models, starting with one byte per
/// character. The agent replies in the model it detected, so the model detected on the
/// reply string is the one that worked; it is then kept for the rest of the connection.
/// </para>
/// </remarks>
internal sealed class AgentLink : IDisposable
{
    private const int HelloIntervalMilliseconds = 2000;
    private static readonly TimeSpan DiscoveryPeriod = TimeSpan.FromSeconds(60);

    private readonly RdpControl _control;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = HelloIntervalMilliseconds };
    private readonly ChannelReceiver _receiver = new();

    private ChannelByteModel _nextHelloModel = ChannelByteModel.OneBytePerChar;
    private DateTime _discoveryDeadline;
    private int _hellosSent;
    private bool _connected;

    public AgentLink(RdpControl control)
    {
        _control = control;
        _control.OnChannelReceivedData += OnChannelReceivedData;
        _timer.Tick += (_, _) => OnTimer();
    }

    /// <summary>Raised when <see cref="State"/> changes, and when the agent answers again.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised for every dropResult message.</summary>
    public event EventHandler<DropResultMessage>? DropResultReceived;

    public AgentState State { get; private set; } = AgentState.NotConnected;

    /// <summary>Version reported by the agent, or null before it answers.</summary>
    public string? AgentVersion { get; private set; }

    /// <summary>Whether the agent sees at least one redirected drive, or null before it answers.</summary>
    public bool? DriveRedirectionAvailable { get; private set; }

    /// <summary>Byte model locked when the agent answered, Unknown before.</summary>
    public ChannelByteModel Model => _receiver.LockedModel;

    /// <summary>
    /// Starts agent discovery. Called on OnConnected.
    /// </summary>
    public void Start()
    {
        _connected = true;
        ResetProtocol();
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
    /// Sends a drop message. Returns false when the agent is not ready or the control
    /// refused the data.
    /// </summary>
    public bool SendDrop(DropMessage drop)
    {
        if (State != AgentState.Ready || Model == ChannelByteModel.Unknown)
        {
            return false;
        }

        return Send(drop, Model, $"drop {drop.Id}");
    }

    public void Dispose()
    {
        _timer.Dispose();
        _control.OnChannelReceivedData -= OnChannelReceivedData;
    }

    private void ResetProtocol()
    {
        _receiver.Reset();
        _nextHelloModel = ChannelByteModel.OneBytePerChar;
        _hellosSent = 0;
        AgentVersion = null;
        DriveRedirectionAvailable = null;
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
        var model = Model;
        if (model == ChannelByteModel.Unknown)
        {
            model = _nextHelloModel;
            _nextHelloModel = model == ChannelByteModel.OneBytePerChar ? ChannelByteModel.Utf16 : ChannelByteModel.OneBytePerChar;
        }

        _hellosSent++;
        Send(HelloMessage.FromClient(AppInfo.Version), model, Invariant($"hello #{_hellosSent}"));
    }

    private bool Send(Message message, ChannelByteModel model, string what)
    {
        string controlString;
        try
        {
            controlString = ChannelTextCodec.TextToControlString(LineFraming.EncodeLine(message), model);
        }
        catch (ArgumentException ex)
        {
            ClientLog.Error($"Cannot encode {what}.", ex);
            return false;
        }

        try
        {
            _control.SendOnVirtualChannel(ProtocolConstants.ChannelName, controlString);
            ClientLog.Info(Invariant($"Sent {what} in model {model}, {controlString.Length} string units."));
            return true;
        }
        catch (Exception ex) when (ex is COMException or AxHost.InvalidActiveXStateException)
        {
            ClientLog.Error($"SendOnVirtualChannel failed for {what}.", ex);
            return false;
        }
    }

    private void OnChannelReceivedData(object? sender, IMsTscAxEvents_OnChannelReceivedDataEvent e)
    {
        if (!string.Equals(e.chanName, ProtocolConstants.ChannelName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var data = e.data ?? string.Empty;
        ClientLog.Info(Invariant($"Received {data.Length} string units on {e.chanName}, first: {FirstUnitsHex(data)}"));
        foreach (var line in _receiver.Receive(data))
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
                ClientLog.Info($"Drop result {result.Id}: {result.Status}, folder \"{result.TargetFolder}\", message \"{result.Message}\"");
                DropResultReceived?.Invoke(this, result);
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
        if (Model == ChannelByteModel.Unknown)
        {
            _receiver.LockedModel = model;
            ClientLog.Info($"Channel byte model locked: {model}.");
        }

        AgentVersion = hello.AgentVersion;
        DriveRedirectionAvailable = hello.DriveRedirectionAvailable;
        _timer.Stop();
        ClientLog.Info($"Agent ready, version {hello.AgentVersion ?? "(unknown)"}, drive redirection available: {hello.DriveRedirectionAvailable?.ToString() ?? "(not said)"}.");
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
}
