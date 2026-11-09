using UltimateRDP.Agent.Drops;
using UltimateRDP.Agent.Localization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Resources;
using UltimateRDP.Core.Protocol;
using Message = UltimateRDP.Core.Protocol.Message;

namespace UltimateRDP.Agent.Channel;

/// <summary>
/// Reacts to the messages sent by the client. Runs on the channel thread; the drag
/// messages go to the <see cref="NativeDropCoordinator"/>, which does the slow work on its
/// own threads, and the log off request to the <see cref="SessionLogoff"/>. The hello of
/// the client sets the language of the texts sent back.
/// </summary>
internal sealed class AgentProtocolHandler
{
    private static readonly byte[] ChunkPrefix = "{\"type\":\"fileChunk\""u8.ToArray();

    private readonly FileLogger _logger;
    private readonly Func<Message, bool> _send;
    private readonly DriveRedirectionProbe _probe;
    private readonly NativeDropCoordinator _native;
    private readonly SessionLogoff _logoff;
    private readonly ClientLanguage _language;

    public AgentProtocolHandler(
        FileLogger logger,
        Func<Message, bool> send,
        DriveRedirectionProbe probe,
        NativeDropCoordinator native,
        SessionLogoff logoff,
        ClientLanguage language)
    {
        _logger = logger;
        _send = send;
        _probe = probe;
        _native = native;
        _logoff = logoff;
        _language = language;
    }

    /// <summary>Handles one line, given as its ASCII bytes.</summary>
    public void Handle(byte[] payload)
    {
        // Chunks are most of the traffic of a transfer and carry no version: they skip the
        // envelope, which would parse each one a second time.
        if (payload.AsSpan().StartsWith(ChunkPrefix) && MessageSerializer.Deserialize(payload) is FileChunkMessage fastChunk)
        {
            _native.OnTransferMessage(fastChunk, fastChunk.TransferId);
            return;
        }

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

        // Every message but the chunks and the moves, at the debug level: it shows which
        // lines arrived.
        if (envelope.Type != MessageTypes.DragMove)
        {
            _logger.Debug($"Received {envelope.Type ?? "(no type)"}{(envelope.Id is null ? string.Empty : " " + envelope.Id)}, {payload.Length} bytes.");
        }
        switch (message)
        {
            case HelloMessage hello:
                OnHello(hello);
                break;

            case DropMessage drop:
                _native.OnDrop(drop);
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

            case LogoffMessage logoff:
                _logoff.OnLogoff(logoff);
                break;

            case TransferBeginMessage begin:
                _native.OnTransferMessage(begin, begin.TransferId);
                break;

            case FileBeginMessage file:
                _native.OnTransferMessage(file, file.TransferId);
                break;

            case FileChunkMessage chunk:
                _native.OnTransferMessage(chunk, chunk.TransferId);
                break;

            case FileEndMessage end:
                _native.OnTransferMessage(end, end.TransferId);
                break;

            case TransferCancelMessage cancel:
                _native.OnTransferMessage(cancel, cancel.TransferId);
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
        var language = _language.Set(hello.ClientLanguage);
        var available = _probe.IsAvailable();
        _logger.Info($"Hello from client {hello.ClientVersion ?? "(unknown version)"}, language {hello.ClientLanguage ?? "(not said)"} (texts in {language}); drive redirection available: {available}.");
        _send(HelloMessage.FromAgent(AgentInfo.Version, available));
    }

    private void RejectNewerVersion(MessageEnvelope envelope)
    {
        _logger.Warn($"The \"{envelope.Type ?? "(none)"}\" message uses protocol version {envelope.Version}; this agent speaks version {ProtocolConstants.ProtocolVersion}.");
        if (!string.IsNullOrEmpty(envelope.Id))
        {
            _send(new DropResultMessage(
                envelope.Id,
                DropStatus.Error,
                Message: LocalText.Of(nameof(Strings.ProtocolMismatch), ProtocolConstants.ProtocolVersion, envelope.Version).In(_language.Culture)));
        }
    }
}
