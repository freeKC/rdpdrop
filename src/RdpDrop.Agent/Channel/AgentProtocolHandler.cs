using RdpDrop.Agent.Drops;
using RdpDrop.Agent.Logging;
using RdpDrop.Core.Protocol;
using Message = RdpDrop.Core.Protocol.Message;

namespace RdpDrop.Agent.Channel;

/// <summary>
/// Reacts to the messages sent by the client. Runs on the channel thread, so anything
/// slow (a drop) goes to its own thread and replies when it is done.
/// </summary>
internal sealed class AgentProtocolHandler
{
    private readonly FileLogger _logger;
    private readonly Func<Message, bool> _send;
    private readonly DriveRedirectionProbe _probe;
    private readonly DropExecutor _executor;
    private readonly NativeDropCoordinator _native;

    public AgentProtocolHandler(FileLogger logger, Func<Message, bool> send, DriveRedirectionProbe probe, DropExecutor executor, NativeDropCoordinator native)
    {
        _logger = logger;
        _send = send;
        _probe = probe;
        _executor = executor;
        _native = native;
    }

    /// <summary>Handles one line, given as its ASCII bytes.</summary>
    public void Handle(byte[] payload)
    {
        var envelope = MessageEnvelope.TryRead(payload);
        if (envelope is null)
        {
            _logger.Warn($"Protocol error: a {payload.Length} byte line is not a JSON object. Ignored.");
            return;
        }

        if (envelope.Version > ProtocolConstants.ProtocolVersion)
        {
            RejectNewerVersion(envelope);
            return;
        }

        var message = MessageSerializer.Deserialize(payload);
        switch (message)
        {
            case HelloMessage hello:
                OnHello(hello);
                break;

            case DropMessage drop:
                OnDrop(drop);
                break;

            case DragEnterMessage enter:
                _native.OnDragEnter(enter);
                break;

            case DragMoveMessage move:
                _native.OnDragMove(move);
                break;

            case DragLeaveMessage leave:
                _native.OnDragLeave(leave);
                break;

            case null:
                _logger.Warn($"Protocol error: the \"{envelope.Type ?? "(none)"}\" message is unknown or malformed. Ignored.");
                break;

            default:
                _logger.Warn($"Unexpected \"{message.Type}\" message from the client. Ignored.");
                break;
        }
    }

    private void OnHello(HelloMessage hello)
    {
        var available = _probe.IsAvailable();
        _logger.Info($"Hello from client {hello.ClientVersion ?? "(unknown version)"}; drive redirection available: {available}.");
        _send(HelloMessage.FromAgent(AgentInfo.Version, available, nativeDrop: true));
    }

    private void OnDrop(DropMessage drop)
    {
        // A drop that ends a live drag is delivered inside the session; any other drop
        // is the 0.1 copy to the folder under the cursor.
        if (_native.TryDrop(drop))
        {
            return;
        }

        _logger.Info($"Drop {drop.Id}: {drop.Items.Count} item(s) at ({drop.X}, {drop.Y}), copy to the folder under the cursor.");
        StaThread.Start("RdpDrop drop " + drop.Id, () => RunDrop(drop));
    }

    private void RunDrop(DropMessage drop)
    {
        DropResultMessage result;
        try
        {
            result = _executor.Execute(drop);
        }
        catch (Exception ex)
        {
            _logger.Error($"Drop {drop.Id} failed unexpectedly.", ex);
            result = new DropResultMessage(drop.Id, DropStatus.Error, Message: "The agent hit an unexpected error: " + ex.Message);
        }

        _logger.Info($"Drop {drop.Id} finished: {result.Status}. {result.Message}");
        _send(result);
    }

    private void RejectNewerVersion(MessageEnvelope envelope)
    {
        _logger.Warn($"The \"{envelope.Type ?? "(none)"}\" message uses protocol version {envelope.Version}; this agent speaks version {ProtocolConstants.ProtocolVersion}.");
        if (!string.IsNullOrEmpty(envelope.Id))
        {
            _send(new DropResultMessage(
                envelope.Id,
                DropStatus.Error,
                Message: $"The RdpDrop agent in the session is older than the client (protocol {ProtocolConstants.ProtocolVersion}, client protocol {envelope.Version}). Update the agent."));
        }
    }
}
