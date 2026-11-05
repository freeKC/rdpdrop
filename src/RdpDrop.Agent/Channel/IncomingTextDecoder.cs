using RdpDrop.Core.Protocol;

namespace RdpDrop.Agent.Channel;

/// <summary>
/// Turns the bytes read on one connection into text. The byte model is detected from
/// the first two bytes and kept until <see cref="Reset"/>. In the UTF-16 model an odd
/// trailing byte waits for the next read.
/// </summary>
internal sealed class IncomingTextDecoder
{
    private byte[] _carry = [];

    /// <summary>The model of the current connection, or Unknown before the first two bytes.</summary>
    public ChannelByteModel Model { get; private set; }

    /// <summary>
    /// Returns the text carried by <paramref name="chunk"/>, possibly empty.
    /// <paramref name="rejected"/> is true when the first bytes of the connection do not
    /// start a message; those bytes are dropped and detection starts again.
    /// </summary>
    public string Decode(ReadOnlySpan<byte> chunk, out bool rejected)
    {
        rejected = false;
        ReadOnlySpan<byte> bytes = chunk;
        if (_carry.Length > 0)
        {
            byte[] merged = [.. _carry, .. chunk];
            bytes = merged;
            _carry = [];
        }

        if (Model == ChannelByteModel.Unknown)
        {
            Model = ChannelTextCodec.DetectFromBytes(bytes);
            if (Model == ChannelByteModel.Unknown)
            {
                if (bytes.Length >= 2)
                {
                    rejected = true;
                }
                else
                {
                    _carry = bytes.ToArray();
                }

                return string.Empty;
            }
        }

        var usable = ChannelTextCodec.UsableLength(bytes, Model);
        if (usable < bytes.Length)
        {
            _carry = bytes[usable..].ToArray();
        }

        return ChannelTextCodec.BytesToText(bytes[..usable], Model);
    }

    /// <summary>Forgets the model and any pending byte, for a new connection.</summary>
    public void Reset()
    {
        _carry = [];
        Model = ChannelByteModel.Unknown;
    }
}
