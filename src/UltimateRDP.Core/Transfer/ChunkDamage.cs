using System.Globalization;

namespace UltimateRDP.Core.Transfer;

/// <summary>
/// A numbered chunk line that was read, but whose data or position cannot be right: the
/// channel damaged it on the way. Since 0.5.2 the agent handles it as a lost line and asks
/// the client for it again (<see cref="TransferReceiver.Handle(Protocol.Message, out ChunkDamage?)"/>).
/// The fields say what the damage looks like, for the log; the line itself is not kept.
/// </summary>
/// <param name="Seq">Number of the line in the transfer.</param>
/// <param name="FileIndex">The file the line names.</param>
/// <param name="Offset">The position in the file the line names.</param>
/// <param name="Encoding">The encoding the data was checked in, one of <see cref="ChunkEncodings"/>.</param>
/// <param name="Failure">
/// How the transfer would have failed before 0.5.2: <see cref="TransferFailure.DataLost"/>
/// for a chunk that does not start where the file stands, <see cref="TransferFailure.BadData"/>
/// otherwise.
/// </param>
/// <param name="Detail">What is wrong, in English, as the receiver says it when it ends the transfer.</param>
/// <param name="Units">Characters of data in the line: units of <see cref="U15Codec"/>, or base64 characters.</param>
/// <param name="ExpectedBytes">
/// The bytes the line should carry: its <c>length</c> field, which a base64 line has since
/// 0.5.2; for a base64 line without it, the chunk size seen before it
/// in the transfer, at most the rest of the file. Null when it is not known: such a line
/// before any chunk, or a length out of range.
/// </param>
/// <param name="ExpectedUnits">Characters of data for <paramref name="ExpectedBytes"/>, or null.</param>
/// <param name="BadUnitIndex">
/// Position in the data of the first character outside the alphabet of the encoding: a unit
/// outside 0x1000 to 0x8FFF in <c>u15</c>, a character that base64 does not use, or a
/// padding character before the end. Null when every character belongs to it.
/// </param>
/// <param name="BadUnit">The character at <paramref name="BadUnitIndex"/>, or zero.</param>
/// <param name="LineLength">
/// Characters of the line without its line break, as the client writes it with the fields
/// and the data that were read.
/// </param>
/// <param name="ExpectedLineLength">Characters of the line that carries <paramref name="ExpectedUnits"/>, or null.</param>
public sealed record ChunkDamage(
    long Seq,
    int FileIndex,
    long Offset,
    string Encoding,
    TransferFailure Failure,
    string Detail,
    int Units,
    int? ExpectedBytes,
    int? ExpectedUnits,
    int? BadUnitIndex,
    char BadUnit,
    int LineLength,
    int? ExpectedLineLength)
{
    /// <summary>
    /// The damage in one sentence of English for the log, from the number of the line:
    /// <c>line 82 arrived damaged: A chunk at byte 2621440 is not valid u15 of 1 to 262144
    /// bytes. Chunk of file 0 at byte 2621440 in u15: 17476 unit(s) of data where 17477
    /// were expected for 32768 bytes, every unit in the range; line of 17597 characters
    /// where 17598 were expected for that chunk size.</c>
    /// </summary>
    public string Describe()
    {
        var u15 = Encoding == ChunkEncodings.U15;
        var unit = u15 ? "unit(s)" : "character(s)";
        var data = (ExpectedUnits, ExpectedBytes) switch
        {
            ({ } units, { } bytes) when units == Units => Invariant($"{Units} {unit} of data, as expected for {bytes} bytes"),
            ({ } units, { } bytes) => Invariant($"{Units} {unit} of data where {units} were expected for {bytes} bytes"),
            _ => Invariant($"{Units} {unit} of data, the expected count is not known"),
        };
        var alphabet = BadUnitIndex switch
        {
            { } index when u15 => Invariant($"first unit outside the range at {index}, 0x{(int)BadUnit:X4}"),
            { } index => Invariant($"first invalid character at {index}, 0x{(int)BadUnit:X4}"),
            null when u15 => "every unit in the range",
            null => "no invalid character",
        };
        var line = ExpectedLineLength switch
        {
            { } expected when expected == LineLength => Invariant($"line of {LineLength} characters, as expected for that chunk size"),
            { } expected => Invariant($"line of {LineLength} characters where {expected} were expected for that chunk size"),
            null => Invariant($"line of {LineLength} characters, the expected length is not known"),
        };
        return Invariant($"line {Seq} arrived damaged: {Detail} Chunk of file {FileIndex} at byte {Offset} in {Encoding}: {data}, {alphabet}; {line}.");
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
