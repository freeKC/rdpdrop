using System.Buffers.Binary;

namespace RdpDrop.Core.Protocol;

/// <summary>
/// Frame layout: a 4 byte little endian payload length followed by the payload.
/// </summary>
public static class Framing
{
    /// <summary>Size of the length prefix in bytes.</summary>
    public const int HeaderSize = 4;

    /// <summary>Largest accepted payload: 1 MiB.</summary>
    public const int MaxFrameSize = 1024 * 1024;

    /// <summary>
    /// Returns the length prefix followed by <paramref name="payload"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The payload is larger than <see cref="MaxFrameSize"/>.</exception>
    public static byte[] Frame(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > MaxFrameSize)
        {
            throw new ArgumentException(
                $"The payload is {payload.Length} bytes; the maximum is {MaxFrameSize} bytes.",
                nameof(payload));
        }

        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, HeaderSize);
        return frame;
    }

    /// <summary>
    /// Serializes <paramref name="message"/> and frames it.
    /// </summary>
    public static byte[] Frame(Message message) => Frame(MessageSerializer.Serialize(message));
}
