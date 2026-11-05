using System.Text.Json.Serialization;

namespace RdpDrop.Core.Protocol;

/// <summary>
/// Base type of every message exchanged on the channel. The JSON form carries a
/// <c>type</c> field that selects the concrete record.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), MessageTypes.Hello)]
[JsonDerivedType(typeof(DropMessage), MessageTypes.Drop)]
[JsonDerivedType(typeof(DropResultMessage), MessageTypes.DropResult)]
public abstract record Message
{
    /// <summary>
    /// Creates a message of the given type.
    /// </summary>
    protected Message(string type)
    {
        Type = type;
    }

    /// <summary>
    /// Value of the <c>type</c> field for this message.
    /// </summary>
    [JsonIgnore]
    public string Type { get; }

    /// <summary>
    /// Checks the values that the JSON layer cannot check on its own.
    /// </summary>
    internal virtual bool IsValid() => true;
}

/// <summary>
/// Values of the <c>type</c> field.
/// </summary>
public static class MessageTypes
{
    /// <summary>Type of <see cref="HelloMessage"/>.</summary>
    public const string Hello = "hello";

    /// <summary>Type of <see cref="DropMessage"/>.</summary>
    public const string Drop = "drop";

    /// <summary>Type of <see cref="DropResultMessage"/>.</summary>
    public const string DropResult = "dropResult";
}

/// <summary>
/// Sent by both sides. The client fills <see cref="ClientVersion"/>; the agent fills
/// <see cref="AgentVersion"/> and <see cref="DriveRedirectionAvailable"/>.
/// Fields left null are not written.
/// </summary>
public sealed record HelloMessage(
    int Version,
    string? ClientVersion = null,
    string? AgentVersion = null,
    bool? DriveRedirectionAvailable = null) : Message(MessageTypes.Hello)
{
    /// <summary>
    /// Builds the hello sent by the client.
    /// </summary>
    public static HelloMessage FromClient(string clientVersion) =>
        new(ProtocolConstants.ProtocolVersion, ClientVersion: clientVersion);

    /// <summary>
    /// Builds the hello sent by the agent.
    /// </summary>
    public static HelloMessage FromAgent(string agentVersion, bool driveRedirectionAvailable) =>
        new(ProtocolConstants.ProtocolVersion, AgentVersion: agentVersion, DriveRedirectionAvailable: driveRedirectionAvailable);
}

/// <summary>
/// Sent by the client when the user drops items. <see cref="X"/> and <see cref="Y"/>
/// are pixels in the remote desktop coordinate space.
/// </summary>
public sealed record DropMessage(string Id, int X, int Y, IReadOnlyList<DropItem> Items) : Message(MessageTypes.Drop)
{
    /// <summary>
    /// Two drop messages are equal when their fields are equal and their items are
    /// equal in the same order.
    /// </summary>
    public bool Equals(DropMessage? other) =>
        other is not null
        && Id == other.Id
        && X == other.X
        && Y == other.Y
        && Items.SequenceEqual(other.Items);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(X);
        hash.Add(Y);
        foreach (var item in Items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    internal override bool IsValid() =>
        !string.IsNullOrEmpty(Id)
        && Items is not null
        && Items.All(item => item is not null && !string.IsNullOrEmpty(item.ClientPath));
}

/// <summary>
/// One file or folder of a drop, with its path on the client computer.
/// </summary>
public sealed record DropItem(string ClientPath, bool IsDirectory);

/// <summary>
/// Sent by the agent once a drop is finished.
/// </summary>
public sealed record DropResultMessage(
    string Id,
    DropStatus Status,
    string? TargetFolder = null,
    string? Message = null) : Message(MessageTypes.DropResult)
{
    internal override bool IsValid() => !string.IsNullOrEmpty(Id) && Enum.IsDefined(Status);
}

/// <summary>
/// Outcome of a drop. Written as <c>"ok"</c>, <c>"error"</c> or <c>"cancelled"</c>.
/// </summary>
public enum DropStatus
{
    /// <summary>Every item was copied.</summary>
    [JsonStringEnumMemberName("ok")]
    Ok,

    /// <summary>The drop failed; the message says why.</summary>
    [JsonStringEnumMemberName("error")]
    Error,

    /// <summary>The user cancelled the copy.</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
}
