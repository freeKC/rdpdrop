using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Protocol;

/// <summary>
/// Sent by the agent since 0.9.0, only to a client whose hello said
/// <see cref="HelloMessage.AcceptsFiles"/>: files of the session to send to the client
/// computer through the direct connection. The agent listed them and listens for the client
/// when it sends this; the client answers with a <see cref="SendAckMessage"/>, then opens the
/// connections of <see cref="Direct"/> and receives the files. A client before 0.9.0 does not
/// know the type, logs it and ignores it, but it never gets one.
/// </summary>
/// <param name="Id">Identifier of the send, repeated by the answers.</param>
/// <param name="Files">Number of files, folders being sent file by file.</param>
/// <param name="Bytes">Total size of the files.</param>
/// <param name="FirstItem">Name of the first item, for the progress window.</param>
/// <param name="Items">The items sent, by name: every relative path of the transfer starts with one of them.</param>
/// <param name="Direct">The direct connection, of version 2, that carries the files.</param>
public sealed record SendOfferMessage(string Id, int Files, long Bytes, string FirstItem, IReadOnlyList<TransferItem> Items, DirectTransferOffer Direct) : Message(MessageTypes.SendOffer)
{
    /// <summary>Most items of one send.</summary>
    public const int MaxItems = 10_000;

    /// <summary>Two offers are equal when their fields are equal and their items are equal in the same order.</summary>
    public bool Equals(SendOfferMessage? other) =>
        other is not null
        && Id == other.Id
        && Files == other.Files
        && Bytes == other.Bytes
        && FirstItem == other.FirstItem
        && Direct == other.Direct
        && Items.SequenceEqual(other.Items);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Files);
        hash.Add(Bytes);
        hash.Add(FirstItem);
        hash.Add(Direct);
        foreach (var item in Items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    internal override bool IsValid() =>
        !string.IsNullOrEmpty(Id)
        && Files >= 0
        && Bytes >= 0
        && FirstItem is not null
        && Items is { Count: > 0 and <= MaxItems }
        && Items.All(item => item is not null && TransferPaths.IsValidName(item.Name))
        && Items.DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Count() == Items.Count
        && Direct is not null;
}

/// <summary>
/// Sent by the client since 0.9.0 in answer to a <see cref="SendOfferMessage"/>: whether it
/// receives the files. When it does, it opens the direct connection next.
/// </summary>
/// <param name="Id">Identifier of the send.</param>
/// <param name="Accepted">True when the client receives the files.</param>
/// <param name="Message">Why the client refused, in the language of the client. Not written when null.</param>
public sealed record SendAckMessage(string Id, bool Accepted, string? Message = null) : Message(MessageTypes.SendAck)
{
    internal override bool IsValid() => !string.IsNullOrEmpty(Id);
}

/// <summary>
/// Sent by either side since 0.9.0: the send stops. The client sends it when the user
/// cancels before the direct connection carries anything, or when the connection cannot be
/// made; the agent when it gives up an offer the client accepted. Once the connection runs,
/// its own cancel and final frames end the transfer; this message follows for the log.
/// </summary>
/// <param name="Id">Identifier of the send.</param>
/// <param name="Reason">Why, in English, for the log. Not written when null.</param>
/// <param name="NotAvailable">True when the direct connection could not be made. Not written when null.</param>
public sealed record SendCancelMessage(string Id, string? Reason = null, bool? NotAvailable = null) : Message(MessageTypes.SendCancel)
{
    internal override bool IsValid() => !string.IsNullOrEmpty(Id);
}
