using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Protocol;

/// <summary>
/// Sent by the client right after a deferred <see cref="DropMessage"/>, before the first
/// file: how many files and bytes the transfer holds. The agent checks the free space with
/// these totals and knows when the last file arrived.
/// </summary>
/// <param name="TransferId">Identifier given by the agent in its <see cref="DragAckMessage"/>.</param>
/// <param name="Files">Number of files that follow, folders being sent file by file.</param>
/// <param name="Bytes">Total size of these files.</param>
/// <param name="ChunkEncoding">
/// How the chunks of the transfer are written, one of <see cref="ChunkEncodings"/>. Null,
/// as a client before 0.5.0 sends it, means base64.
/// </param>
/// <param name="Seq">
/// Number of the line in the transfer, from 0 for <c>transferBegin</c>, dense and in the order
/// of sending (see <see cref="ReliableInbox"/>). Null, as a client before 0.5.1 or a client
/// talking to an agent before 0.5.1 sends it, means the line is handled as it arrives.
/// </param>
public sealed record TransferBeginMessage(string TransferId, int Files, long Bytes, string? ChunkEncoding = null, long? Seq = null) : Message(MessageTypes.TransferBegin)
{
    internal override bool IsValid() => !string.IsNullOrEmpty(TransferId) && Files >= 0 && Bytes >= 0 && Seq is null or >= 0;
}

/// <summary>
/// Sent by the client before the chunks of one file.
/// </summary>
/// <param name="TransferId">Identifier of the transfer.</param>
/// <param name="FileIndex">Position of the file in the transfer, from zero, one after the other.</param>
/// <param name="RelativePath">
/// Path of the file inside the transfer, with <c>\</c> between the segments. The first
/// segment is the name of the dropped item: the file itself, or the dropped folder that
/// holds it.
/// </param>
/// <param name="Size">Size of the file in bytes.</param>
/// <param name="Modified">Last write time of the file, in UTC, so that the copy keeps it. Not written when null.</param>
/// <param name="Seq">Number of the line in the transfer, see <see cref="TransferBeginMessage.Seq"/>.</param>
public sealed record FileBeginMessage(string TransferId, int FileIndex, string RelativePath, long Size, DateTime? Modified = null, long? Seq = null) : Message(MessageTypes.FileBegin)
{
    internal override bool IsValid() =>
        !string.IsNullOrEmpty(TransferId) && FileIndex >= 0 && !string.IsNullOrEmpty(RelativePath) && Size >= 0 && Seq is null or >= 0;
}

/// <summary>
/// Sent by the client: one piece of a file, at most <see cref="TransferSender.MaxChunkSize"/>
/// bytes, encoded as the <c>chunkEncoding</c> of <see cref="TransferBeginMessage"/> says.
/// The client sends <see cref="TransferSender.DefaultChunkSize"/> bytes unless its settings
/// say otherwise. <see cref="TransferLines"/> builds and reads the line of a chunk without
/// the serializer.
/// </summary>
/// <param name="TransferId">Identifier of the transfer.</param>
/// <param name="FileIndex">Position of the file in the transfer.</param>
/// <param name="Offset">Position of the first byte of this piece in the file.</param>
/// <param name="Data">The bytes, in base64, or in the units of <see cref="U15Codec"/> when <paramref name="Enc"/> says so. Written last.</param>
/// <param name="Length">
/// The number of bytes, which ends the data of <see cref="ChunkEncodings.U15"/>. In base64
/// written since 0.5.2 on numbered lines only, where the agent checks it. Not written when null.
/// </param>
/// <param name="Enc">The encoding of <paramref name="Data"/>, one of <see cref="ChunkEncodings"/>. Not written when null, which means base64.</param>
/// <param name="Seq">
/// Number of the line in the transfer, see <see cref="TransferBeginMessage.Seq"/>. Written
/// after the other fields and before <paramref name="Data"/>.
/// </param>
public sealed record FileChunkMessage(
    string TransferId,
    int FileIndex,
    long Offset,
    [property: System.Text.Json.Serialization.JsonPropertyOrder(1)] string Data,
    int? Length = null,
    string? Enc = null,
    long? Seq = null) : Message(MessageTypes.FileChunk)
{
    internal override bool IsValid() =>
        !string.IsNullOrEmpty(TransferId) && FileIndex >= 0 && Offset >= 0 && Data is not null && Length is null or >= 0 && Seq is null or >= 0;
}

/// <summary>
/// Sent by the client after the last chunk of a file. The agent checks the size and the
/// checksum, and refuses the file when either one differs.
/// </summary>
/// <param name="TransferId">Identifier of the transfer.</param>
/// <param name="FileIndex">Position of the file in the transfer.</param>
/// <param name="Sha256">SHA-256 of the whole file, 64 hexadecimal digits in lower case.</param>
/// <param name="Seq">Number of the line in the transfer, see <see cref="TransferBeginMessage.Seq"/>.</param>
public sealed record FileEndMessage(string TransferId, int FileIndex, string Sha256, long? Seq = null) : Message(MessageTypes.FileEnd)
{
    internal override bool IsValid() =>
        !string.IsNullOrEmpty(TransferId) && FileIndex >= 0 && TransferHash.IsValidHex(Sha256) && Seq is null or >= 0;
}

/// <summary>
/// Sent by the client to stop a transfer. The agent deletes what it received and answers
/// with a cancelled <see cref="TransferResultMessage"/>.
/// </summary>
/// <param name="TransferId">Identifier of the transfer.</param>
/// <param name="Reason">Why, in English, for the log of the agent.</param>
public sealed record TransferCancelMessage(string TransferId, string? Reason = null) : Message(MessageTypes.TransferCancel)
{
    internal override bool IsValid() => !string.IsNullOrEmpty(TransferId);
}

/// <summary>
/// Sent by the agent while it receives a file: after the first chunk of each file, at
/// least every <see cref="TransferReceiver.AckInterval"/> bytes and at the end of each file. The
/// client keeps at most <see cref="TransferSender.WindowBytes"/> bytes sent and not yet
/// acknowledged, 4 MiB by default.
/// </summary>
/// <param name="TransferId">Identifier of the transfer.</param>
/// <param name="FileIndex">Position of the file in the transfer.</param>
/// <param name="Received">Bytes of this file received and written so far.</param>
/// <param name="Seq">
/// Since 0.5.1, for a transfer whose lines carry a number: every line up to this number was
/// processed, so the client no longer needs to keep them for a resend. Not written when null.
/// </param>
public sealed record FileAckMessage(string TransferId, int FileIndex, long Received, long? Seq = null) : Message(MessageTypes.FileAck)
{
    internal override bool IsValid() => !string.IsNullOrEmpty(TransferId) && FileIndex >= 0 && Received >= 0 && Seq is null or >= 0;
}

/// <summary>
/// Sent by the agent since 0.5.1 when lines of a transfer are missing: a later line arrived
/// first, so the lines from <see cref="From"/> to <see cref="To"/> were lost on the way. The
/// client sends them again, in order. See <see cref="ReliableInbox"/>.
/// </summary>
/// <param name="TransferId">Identifier of the transfer.</param>
/// <param name="From">Number of the first missing line.</param>
/// <param name="To">Number of the last missing line, at least <paramref name="From"/>.</param>
/// <param name="Damaged">
/// True since 0.5.2 when line <paramref name="From"/> arrived but its chunk was damaged on
/// the way, which the agent handles as a lost line; not written otherwise. For the logs and
/// the counts of the client only: the lines go again the same way.
/// </param>
public sealed record TransferResendMessage(string TransferId, long From, long To, bool? Damaged = null) : Message(MessageTypes.TransferResend)
{
    internal override bool IsValid() => !string.IsNullOrEmpty(TransferId) && From >= 0 && To >= From;
}

/// <summary>
/// Sent by the agent once a transfer is over: every file arrived and was checked
/// (<see cref="DropStatus.Ok"/>), it failed (<see cref="DropStatus.Error"/>, with the
/// reason) or it was cancelled. After an ok result the agent drops the copies and sends
/// the usual <see cref="DropResultMessage"/>.
/// </summary>
/// <param name="TransferId">Identifier of the transfer.</param>
/// <param name="Status">Outcome of the transfer.</param>
/// <param name="Message">Human readable text, in the language of the client.</param>
/// <param name="Bytes">Bytes received.</param>
/// <param name="Seconds">Time from the start of the transfer to its end, measured by the agent.</param>
public sealed record TransferResultMessage(string TransferId, DropStatus Status, string? Message = null, long Bytes = 0, double Seconds = 0) : Message(MessageTypes.TransferResult)
{
    internal override bool IsValid() =>
        !string.IsNullOrEmpty(TransferId) && Enum.IsDefined(Status) && Bytes >= 0 && Seconds >= 0 && double.IsFinite(Seconds);
}
