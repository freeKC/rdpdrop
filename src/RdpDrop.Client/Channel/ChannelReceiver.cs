using RdpDrop.Client.Logging;
using RdpDrop.Core.Protocol;

namespace RdpDrop.Client.Channel;

/// <summary>
/// One complete line received from the agent, with the byte model it was decoded with.
/// </summary>
internal readonly record struct ReceivedLine(string Text, ChannelByteModel Model);

/// <summary>
/// Turns the strings delivered by OnChannelReceivedData into text lines.
/// </summary>
/// <remarks>
/// A string that starts a line (nothing buffered) tells its own model, because a line
/// always starts with '{'. A string that continues a line uses the model of the previous
/// one. Once <see cref="LockedModel"/> is set, a string that starts a line in the other
/// model does not belong to this connection and is ignored.
/// </remarks>
internal sealed class ChannelReceiver
{
    private readonly LineReader _reader = new();
    private ChannelByteModel _receiveModel = ChannelByteModel.Unknown;

    public ChannelReceiver()
    {
        _reader.ProtocolError += (_, _) => ClientLog.Warn("Protocol error: a line from the agent is longer than the limit; buffer dropped.");
    }

    /// <summary>Model kept for the connection, or Unknown while it is not decided.</summary>
    public ChannelByteModel LockedModel { get; set; } = ChannelByteModel.Unknown;

    public void Reset()
    {
        LockedModel = ChannelByteModel.Unknown;
        _receiveModel = ChannelByteModel.Unknown;
        _reader.Reset();
    }

    /// <summary>
    /// Adds a received string and returns the lines it completes.
    /// </summary>
    public List<ReceivedLine> Receive(string data)
    {
        var lines = new List<ReceivedLine>();
        if (string.IsNullOrEmpty(data))
        {
            return lines;
        }

        var model = ChooseModel(data);
        if (model == ChannelByteModel.Unknown)
        {
            return lines;
        }

        string text;
        try
        {
            text = ChannelTextCodec.ControlStringToText(data, model);
        }
        catch (ArgumentException ex)
        {
            ClientLog.Warn($"Protocol error: the received string does not fit the {model} model ({ex.Message}). Buffer dropped.");
            _reader.Reset();
            return lines;
        }

        _reader.Append(text);
        while (_reader.TryReadLine(out var line))
        {
            // A NUL left at the edge of a line (a string terminator, or a string decoded
            // in the wrong model) must not spoil it.
            var trimmed = line.Trim(['\0', ' ']);
            if (trimmed.Length > 0)
            {
                lines.Add(new ReceivedLine(trimmed, model));
            }
        }

        return lines;
    }

    private ChannelByteModel ChooseModel(string data)
    {
        if (_reader.BufferedLength > 0 && _receiveModel != ChannelByteModel.Unknown)
        {
            return _receiveModel;
        }

        var detected = ChannelTextCodec.DetectFromControlString(data);
        if (LockedModel != ChannelByteModel.Unknown && detected != LockedModel)
        {
            ClientLog.Info($"Ignored a string in the {detected} model; the connection uses {LockedModel}.");
            return ChannelByteModel.Unknown;
        }

        if (detected != _receiveModel)
        {
            ClientLog.Info($"Channel byte model detected on received data: {detected}.");
        }

        _receiveModel = detected;
        return detected;
    }
}
