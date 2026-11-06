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
[JsonDerivedType(typeof(DragEnterMessage), MessageTypes.DragEnter)]
[JsonDerivedType(typeof(DragMoveMessage), MessageTypes.DragMove)]
[JsonDerivedType(typeof(DragLeaveMessage), MessageTypes.DragLeave)]
[JsonDerivedType(typeof(DragAckMessage), MessageTypes.DragAck)]
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

    /// <summary>Type of <see cref="DragEnterMessage"/>.</summary>
    public const string DragEnter = "dragEnter";

    /// <summary>Type of <see cref="DragMoveMessage"/>.</summary>
    public const string DragMove = "dragMove";

    /// <summary>Type of <see cref="DragLeaveMessage"/>.</summary>
    public const string DragLeave = "dragLeave";

    /// <summary>Type of <see cref="DragAckMessage"/>.</summary>
    public const string DragAck = "dragAck";
}

/// <summary>
/// Sent by both sides. The client fills <see cref="ClientVersion"/>; the agent fills
/// <see cref="AgentVersion"/>, <see cref="DriveRedirectionAvailable"/> and
/// <see cref="NativeDrop"/>. Fields left null are not written.
/// </summary>
/// <param name="Version">Protocol version, see <see cref="ProtocolConstants.ProtocolVersion"/>.</param>
/// <param name="ClientVersion">Version of the client, in the hello of the client.</param>
/// <param name="AgentVersion">Version of the agent, in the hello of the agent.</param>
/// <param name="DriveRedirectionAvailable">Whether the agent sees a redirected drive.</param>
/// <param name="NativeDrop">
/// True when the agent understands the live drag messages (<c>dragEnter</c>,
/// <c>dragMove</c>, <c>dragLeave</c>) and replays the drop inside the session. Absent
/// in the hello of a 0.1 agent.
/// </param>
public sealed record HelloMessage(
    int Version,
    string? ClientVersion = null,
    string? AgentVersion = null,
    bool? DriveRedirectionAvailable = null,
    bool? NativeDrop = null) : Message(MessageTypes.Hello)
{
    /// <summary>
    /// Builds the hello sent by the client.
    /// </summary>
    public static HelloMessage FromClient(string clientVersion) =>
        new(ProtocolConstants.ProtocolVersion, ClientVersion: clientVersion);

    /// <summary>
    /// Builds the hello sent by the agent.
    /// </summary>
    public static HelloMessage FromAgent(string agentVersion, bool driveRedirectionAvailable, bool? nativeDrop = null) =>
        new(ProtocolConstants.ProtocolVersion, AgentVersion: agentVersion, DriveRedirectionAvailable: driveRedirectionAvailable, NativeDrop: nativeDrop);
}

/// <summary>
/// Flags of the <c>keyState</c> field: the modifier keys held on the client computer.
/// </summary>
public static class KeyStates
{
    /// <summary>No modifier key.</summary>
    public const int None = 0;

    /// <summary>Ctrl is held.</summary>
    public const int Control = 1;

    /// <summary>Shift is held.</summary>
    public const int Shift = 2;

    /// <summary>Alt is held.</summary>
    public const int Alt = 4;

    /// <summary>Every flag defined by this version.</summary>
    public const int All = Control | Shift | Alt;

    /// <summary>Readable form of a key state, for example "Ctrl+Shift", or "none".</summary>
    public static string Describe(int keyState)
    {
        var parts = new List<string>(3);
        if ((keyState & Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((keyState & Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((keyState & Alt) != 0)
        {
            parts.Add("Alt");
        }

        return parts.Count == 0 ? "none" : string.Join("+", parts);
    }
}

/// <summary>
/// Sent by the client when the user drops items. <see cref="X"/> and <see cref="Y"/>
/// are pixels in the remote desktop coordinate space. After a <see cref="DragEnterMessage"/>
/// with the same id, it ends the live drag; on its own it asks for the 0.1 copy to the
/// folder under the cursor.
/// </summary>
/// <param name="Id">Identifier of the drop, shared with the live drag messages.</param>
/// <param name="X">Horizontal position in the remote desktop.</param>
/// <param name="Y">Vertical position in the remote desktop.</param>
/// <param name="Items">Dropped files and folders.</param>
/// <param name="KeyState">Modifier keys, see <see cref="KeyStates"/>. Zero when absent.</param>
public sealed record DropMessage(string Id, int X, int Y, IReadOnlyList<DropItem> Items, int KeyState = KeyStates.None) : Message(MessageTypes.Drop)
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
        && KeyState == other.KeyState
        && Items.SequenceEqual(other.Items);

    /// <inheritdoc />
    public override int GetHashCode() => ItemHash.Combine(Id, X, Y, KeyState, Items);

    internal override bool IsValid() => !string.IsNullOrEmpty(Id) && DropItem.AreValid(Items);
}

/// <summary>
/// One file or folder of a drop, with its path on the client computer.
/// </summary>
/// <param name="ClientPath">Full path on the client computer.</param>
/// <param name="IsDirectory">True for a folder.</param>
public sealed record DropItem(string ClientPath, bool IsDirectory)
{
    internal static bool AreValid(IReadOnlyList<DropItem>? items) =>
        items is not null && items.All(item => item is not null && !string.IsNullOrEmpty(item.ClientPath));
}

/// <summary>
/// Sent by the client on the first drag over over the session, when the agent said
/// <see cref="HelloMessage.NativeDrop"/>. The agent starts a drag inside the session at
/// the point and answers with a <see cref="DragAckMessage"/>.
/// </summary>
/// <param name="Id">Identifier of the drag, reused by the messages that follow.</param>
/// <param name="X">Horizontal position in the remote desktop.</param>
/// <param name="Y">Vertical position in the remote desktop.</param>
/// <param name="Items">Dragged files and folders.</param>
/// <param name="KeyState">Modifier keys, see <see cref="KeyStates"/>. Zero when absent.</param>
public sealed record DragEnterMessage(string Id, int X, int Y, IReadOnlyList<DropItem> Items, int KeyState = KeyStates.None) : Message(MessageTypes.DragEnter)
{
    /// <summary>
    /// Two messages are equal when their fields are equal and their items are equal in
    /// the same order.
    /// </summary>
    public bool Equals(DragEnterMessage? other) =>
        other is not null
        && Id == other.Id
        && X == other.X
        && Y == other.Y
        && KeyState == other.KeyState
        && Items.SequenceEqual(other.Items);

    /// <inheritdoc />
    public override int GetHashCode() => ItemHash.Combine(Id, X, Y, KeyState, Items);

    internal override bool IsValid() => !string.IsNullOrEmpty(Id) && DropItem.AreValid(Items);
}

/// <summary>
/// Sent by the client while the drag moves over the session, at most about thirty times
/// a second and only when the point or the keys changed.
/// </summary>
/// <param name="Id">Identifier given by the <see cref="DragEnterMessage"/>.</param>
/// <param name="X">Horizontal position in the remote desktop.</param>
/// <param name="Y">Vertical position in the remote desktop.</param>
/// <param name="KeyState">Modifier keys, see <see cref="KeyStates"/>. Zero when absent.</param>
public sealed record DragMoveMessage(string Id, int X, int Y, int KeyState = KeyStates.None) : Message(MessageTypes.DragMove)
{
    internal override bool IsValid() => !string.IsNullOrEmpty(Id);
}

/// <summary>
/// Sent by the client when the drag leaves the session or is cancelled. The agent
/// cancels the drag inside the session.
/// </summary>
/// <param name="Id">Identifier given by the <see cref="DragEnterMessage"/>.</param>
public sealed record DragLeaveMessage(string Id) : Message(MessageTypes.DragLeave)
{
    internal override bool IsValid() => !string.IsNullOrEmpty(Id);
}

/// <summary>
/// Sent by the agent in answer to a <see cref="DragEnterMessage"/>.
/// </summary>
/// <param name="Id">Identifier of the drag.</param>
/// <param name="Accepted">True when the drag runs inside the session.</param>
/// <param name="Message">Why the drag was refused, or a note such as a long staging.</param>
public sealed record DragAckMessage(string Id, bool Accepted, string? Message = null) : Message(MessageTypes.DragAck)
{
    internal override bool IsValid() => !string.IsNullOrEmpty(Id);
}

/// <summary>
/// Sent by the agent once a drop is finished.
/// </summary>
/// <param name="Id">Identifier of the drop.</param>
/// <param name="Status">Outcome of the drop.</param>
/// <param name="TargetFolder">Folder that received the copy, when the agent copied.</param>
/// <param name="Message">Human readable text for the status bar.</param>
/// <param name="Effect">What the drop did inside the session. Absent from a 0.1 agent.</param>
/// <param name="TargetWindow">Window that received the drop, when known.</param>
public sealed record DropResultMessage(
    string Id,
    DropStatus Status,
    string? TargetFolder = null,
    string? Message = null,
    DropEffect? Effect = null,
    TargetWindowInfo? TargetWindow = null) : Message(MessageTypes.DropResult)
{
    internal override bool IsValid() =>
        !string.IsNullOrEmpty(Id)
        && Enum.IsDefined(Status)
        && (Effect is null || Enum.IsDefined(Effect.Value));
}

/// <summary>
/// Window under the drop point inside the session.
/// </summary>
/// <param name="ClassName">Class of the top level window.</param>
/// <param name="Title">Title of the top level window, or of its owner.</param>
/// <param name="ChildClassName">Class of the window right under the point, when it differs.</param>
/// <param name="ProcessName">Name of the process that owns the window, when it could be read.</param>
public sealed record TargetWindowInfo(string? ClassName, string? Title, string? ChildClassName = null, string? ProcessName = null);

/// <summary>
/// What a drop did inside the session. Written as <c>"copy"</c>, <c>"link"</c>,
/// <c>"none"</c> or <c>"fallbackCopy"</c>.
/// </summary>
public enum DropEffect
{
    /// <summary>The target accepted the drop as a copy.</summary>
    [JsonStringEnumMemberName("copy")]
    Copy,

    /// <summary>The target accepted the drop as a link.</summary>
    [JsonStringEnumMemberName("link")]
    Link,

    /// <summary>Nothing accepted the drop and nothing was copied.</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>Nothing accepted the drop; the agent copied the items to the fallback folder.</summary>
    [JsonStringEnumMemberName("fallbackCopy")]
    FallbackCopy,
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

/// <summary>Hash of a message that carries a list of items.</summary>
internal static class ItemHash
{
    public static int Combine(string id, int x, int y, int keyState, IReadOnlyList<DropItem> items)
    {
        var hash = new HashCode();
        hash.Add(id);
        hash.Add(x);
        hash.Add(y);
        hash.Add(keyState);
        foreach (var item in items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}
