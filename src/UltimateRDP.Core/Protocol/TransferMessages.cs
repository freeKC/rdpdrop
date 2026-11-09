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
public sealed record TransferBeginMessage(string TransferId, int Files, long Bytes) : Message(MessageTypes.TransferBegin)
{
    internal override bool IsValid() => !string.IsNullOrEmpty(TransferId) && Files >= 0 && Bytes >= 0;
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
public sealed record FileBeginMessage(string TransferId, int FileIndex, string RelativePath, long Size, DateTime? Modified = null) : Message(MessageTypes.FileBegin)
{
    internal override bool IsValid() =>
        !string.IsNullOrEmpty(TransferId) && FileIndex >= 0 && !string.IsNullOrEmpty(RelativePath) && Size >= 0;
}

/// <summary>
/// Sent by the client: one piece of a file, at most <see cref="TransferSender.ChunkSize"/>
/// bytes, encoded in base64.
/// </summary>
/// <param name="TransferId">Identifier of the transfer.</param>
/// <param name="FileIndex">Position of the file in the transfer.</param>
/// <param name="Offset">Position of the first byte of this piece in the file.</param>
/// <param name="Data">The bytes, in base64.</param>
public sealed record FileChunkMessage(string TransferId, int FileIndex, long Offset, string Data) : Message(MessageTypes.FileChunk)
{
    internal override bool IsValid() =>
        !string.IsNullOrEmpty(TransferId) && FileIndex >= 0 && Offset >= 0 && Data is not null;
}

/// <summary>
/// Sent by the client after the last chunk of a file. The agent checks the size and the
/// checksum, and refuses the file when either one differs.
/// </summary>
/// <param name="TransferId">Identifier of the transfer.</param>
/// <param name="FileIndex">Position of the file in the transfer.</param>
/// <param name="Sha256">SHA-256 of the whole file, 64 hexadecimal digits in lower case.</param>
public sealed record FileEndMessage(string TransferId, int FileIndex, string Sha256) : Message(MessageTypes.FileEnd)
{
    internal override bool IsValid() =>
        !string.IsNullOrEmpty(TransferId) && FileIndex >= 0 && TransferHash.IsValidHex(Sha256);
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
/// Sent by the agent while it receives a file: at least every
/// <see cref="TransferReceiver.AckInterval"/> bytes and at the end of each file. The
/// client keeps at most <see cref="TransferSender.WindowBytes"/> bytes sent and not yet
/// acknowledged.
/// </summary>
/// <param name="TransferId">Identifier of the transfer.</param>
/// <param name="FileIndex">Position of the file in the transfer.</param>
/// <param name="Received">Bytes of this file received and written so far.</param>
public sealed record FileAckMessage(string TransferId, int FileIndex, long Received) : Message(MessageTypes.FileAck)
{
    internal override bool IsValid() => !string.IsNullOrEmpty(TransferId) && FileIndex >= 0 && Received >= 0;
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
