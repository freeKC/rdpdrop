using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Transfer;

/// <summary>
/// How the data of a <c>fileChunk</c> is written: the value of <c>chunkEncoding</c> in
/// <c>transferBegin</c> and of <c>enc</c> in each chunk.
/// </summary>
public static class ChunkEncodings
{
    /// <summary>base64 in the JSON string, ASCII only: works in both byte models.</summary>
    public const string Base64 = "base64";

    /// <summary>
    /// 15 bits per UTF-16 code unit (<see cref="U15Codec"/>), with the byte count in
    /// <c>length</c>: only for a channel that carries the units of the client as UTF-16.
    /// </summary>
    public const string U15 = "u15";

    /// <summary>True for an encoding this version reads. Null means <see cref="Base64"/>, as an older client sends it.</summary>
    public static bool IsKnown(string? encoding) => encoding is null or Base64 or U15;

    /// <summary>The encoding a null value stands for.</summary>
    public static string OrDefault(string? encoding) => encoding ?? Base64;

    /// <summary>
    /// The encoding the client chooses for a transfer, from the byte model the agent
    /// observes on the data of the client: <see cref="U15"/> when each character arrives as
    /// two bytes, <see cref="Base64"/> otherwise, including when the agent did not say.
    /// </summary>
    public static string Choose(ChannelByteModel clientToAgent) =>
        clientToAgent == ChannelByteModel.Utf16 ? U15 : Base64;

    /// <summary>
    /// Bytes on the wire per byte of file data, without the fields of the line: 4/3 for
    /// base64 in the one byte per character model, 8/3 for base64 as UTF-16, 16/15 for
    /// <see cref="U15"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The encoding is unknown, or <see cref="U15"/> in a model other than UTF-16.</exception>
    public static double WireBytesPerFileByte(string encoding, ChannelByteModel clientToAgent)
    {
        var unitBytes = clientToAgent switch
        {
            ChannelByteModel.Utf16 => 2.0,
            ChannelByteModel.OneBytePerChar => 1.0,
            _ => throw new ArgumentException("The byte model must be known.", nameof(clientToAgent)),
        };

        return encoding switch
        {
            Base64 => unitBytes * 4 / 3,
            U15 when clientToAgent == ChannelByteModel.Utf16 => 2.0 * 8 / U15Codec.BitsPerUnit,
            _ => throw new ArgumentException($"The encoding \"{encoding}\" cannot travel in the {clientToAgent} model.", nameof(encoding)),
        };
    }
}
