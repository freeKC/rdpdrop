using System.Text.Json.Serialization;

namespace UltimateRDP.Core.Protocol;

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
[JsonDerivedType(typeof(LogoffMessage), MessageTypes.Logoff)]
[JsonDerivedType(typeof(LogoffResultMessage), MessageTypes.LogoffResult)]
[JsonDerivedType(typeof(TransferBeginMessage), MessageTypes.TransferBegin)]
[JsonDerivedType(typeof(FileBeginMessage), MessageTypes.FileBegin)]
[JsonDerivedType(typeof(FileChunkMessage), MessageTypes.FileChunk)]
[JsonDerivedType(typeof(FileEndMessage), MessageTypes.FileEnd)]
[JsonDerivedType(typeof(TransferCancelMessage), MessageTypes.TransferCancel)]
[JsonDerivedType(typeof(FileAckMessage), MessageTypes.FileAck)]
[JsonDerivedType(typeof(TransferResultMessage), MessageTypes.TransferResult)]
[JsonDerivedType(typeof(TransferResendMessage), MessageTypes.TransferResend)]
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

    /// <summary>Type of <see cref="LogoffMessage"/>.</summary>
    public const string Logoff = "logoff";

    /// <summary>Type of <see cref="LogoffResultMessage"/>.</summary>
    public const string LogoffResult = "logoffResult";

    /// <summary>Type of <see cref="TransferBeginMessage"/>.</summary>
    public const string TransferBegin = "transferBegin";

    /// <summary>Type of <see cref="FileBeginMessage"/>.</summary>
    public const string FileBegin = "fileBegin";

    /// <summary>Type of <see cref="FileChunkMessage"/>.</summary>
    public const string FileChunk = "fileChunk";

    /// <summary>Type of <see cref="FileEndMessage"/>.</summary>
    public const string FileEnd = "fileEnd";

    /// <summary>Type of <see cref="TransferCancelMessage"/>.</summary>
    public const string TransferCancel = "transferCancel";

    /// <summary>Type of <see cref="FileAckMessage"/>.</summary>
    public const string FileAck = "fileAck";

    /// <summary>Type of <see cref="TransferResultMessage"/>.</summary>
    public const string TransferResult = "transferResult";

    /// <summary>Type of <see cref="TransferResendMessage"/>.</summary>
    public const string TransferResend = "transferResend";
}

/// <summary>
/// Sent by both sides. The client fills <see cref="ClientVersion"/> and
/// <see cref="ClientLanguage"/>; the agent fills <see cref="AgentVersion"/> and
/// <see cref="DriveRedirectionAvailable"/>, since 0.5.0 <see cref="ClientToAgentModel"/> and
/// since 0.5.1 <see cref="ReliableTransfer"/>. Fields left null are not written.
/// </summary>
/// <param name="Version">Protocol version, see <see cref="ProtocolConstants.ProtocolVersion"/>.</param>
/// <param name="ClientVersion">Version of the client, in the hello of the client.</param>
/// <param name="AgentVersion">Version of the agent, in the hello of the agent.</param>
/// <param name="DriveRedirectionAvailable">Whether the agent sees a redirected drive.</param>
/// <param name="ClientLanguage">
/// Two letter code of the language of the client, in the hello of the client. The agent
/// writes the texts it sends for that language; see <see cref="Localization.UiLanguages"/>.
/// </param>
/// <param name="ClientToAgentModel">
/// In the hello of the agent: the byte model it detected on the data of the client, named
/// as <see cref="ChannelByteModelNames"/> says. The client chooses the encoding of the
/// chunks from it (<see cref="Transfer.ChunkEncodings.Choose"/>); an agent that does not
/// say gets base64.
/// </param>
/// <param name="ReliableTransfer">
/// In the hello of the agent, since 0.5.1: true when the agent puts the lines of a transfer
/// back in the order of their <c>seq</c> and asks for the missing ones with
/// <see cref="TransferResendMessage"/>. Only then does the client number its lines: an agent
/// 0.5.0 would not read a <c>u15</c> chunk line that carries a <c>seq</c>.
/// </param>
public sealed record HelloMessage(
    int Version,
    string? ClientVersion = null,
    string? AgentVersion = null,
    bool? DriveRedirectionAvailable = null,
    string? ClientLanguage = null,
    string? ClientToAgentModel = null,
    bool? ReliableTransfer = null) : Message(MessageTypes.Hello)
{
    /// <summary>
    /// Builds the hello sent by the client.
    /// </summary>
    public static HelloMessage FromClient(string clientVersion, string? clientLanguage = null) =>
        new(ProtocolConstants.ProtocolVersion, ClientVersion: clientVersion, ClientLanguage: clientLanguage);

    /// <summary>
    /// Builds the hello sent by the agent, with the byte model it detected on the data of
    /// the client; <see cref="ChannelByteModel.Unknown"/> leaves the field out.
    /// <paramref name="reliableTransfer"/> says that the agent reads numbered transfer lines.
    /// </summary>
    public static HelloMessage FromAgent(string agentVersion, bool driveRedirectionAvailable, ChannelByteModel clientToAgentModel = ChannelByteModel.Unknown, bool reliableTransfer = false) =>
        new(
            ProtocolConstants.ProtocolVersion,
            AgentVersion: agentVersion,
            DriveRedirectionAvailable: driveRedirectionAvailable,
            ClientToAgentModel: ChannelByteModelNames.ToName(clientToAgentModel),
            ReliableTransfer: reliableTransfer ? true : null);
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
/// Sent by the client when the user releases the button over the session. It ends the
/// live drag started by the <see cref="DragEnterMessage"/> with the same id, which
/// carried the items. <see cref="X"/> and <see cref="Y"/> are pixels in the remote
/// desktop coordinate space.
/// </summary>
/// <param name="Id">Identifier given by the <see cref="DragEnterMessage"/>.</param>
/// <param name="X">Horizontal position in the remote desktop.</param>
/// <param name="Y">Vertical position in the remote desktop.</param>
/// <param name="KeyState">Modifier keys, see <see cref="KeyStates"/>. Zero when absent.</param>
/// <param name="Deferred">
/// True when the agent asked for a transfer in its <see cref="DragAckMessage"/>: the agent
/// ends the live drag without a drop, remembers the point, receives the files and drops
/// the copies there once they arrived. Not written when null.
/// </param>
public sealed record DropMessage(string Id, int X, int Y, int KeyState = KeyStates.None, bool? Deferred = null) : Message(MessageTypes.Drop)
{
    internal override bool IsValid() => !string.IsNullOrEmpty(Id);
}

/// <summary>
/// One file or folder of a drag, with its path on the client computer.
/// </summary>
/// <param name="ClientPath">Full path on the client computer.</param>
/// <param name="IsDirectory">True for a folder.</param>
/// <param name="Size">
/// Size in bytes: the length of a file, or the total length of the files inside a folder
/// as far as the client could count them quickly. Null when unknown. The agent uses it to
/// decide whether the files are transferred through the channel.
/// </param>
public sealed record DropItem(string ClientPath, bool IsDirectory, long? Size = null)
{
    internal static bool AreValid(IReadOnlyList<DropItem>? items) =>
        items is not null && items.All(item => item is not null && !string.IsNullOrEmpty(item.ClientPath) && item.Size is null or >= 0);
}

/// <summary>
/// Sent by the client on the first drag over over the session. The agent starts a drag
/// inside the session at the point and answers with a <see cref="DragAckMessage"/>.
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
/// <param name="Transfer">
/// True when the agent wants the files transferred through the channel before the drop:
/// the client then ends the drag with a deferred <see cref="DropMessage"/> and sends the
/// files under <paramref name="TransferId"/>. Not written when null.
/// </param>
/// <param name="TransferId">Identifier of the transfer, when <paramref name="Transfer"/> is true.</param>
/// <param name="Provisional">
/// True on an accepted answer sent before the agent decided whether the files are
/// transferred, because the check of the items is slow: a final dragAck follows. A drop
/// held by the client waits for that final answer. Not written when null.
/// </param>
public sealed record DragAckMessage(string Id, bool Accepted, string? Message = null, bool? Transfer = null, string? TransferId = null, bool? Provisional = null) : Message(MessageTypes.DragAck)
{
    internal override bool IsValid() =>
        !string.IsNullOrEmpty(Id) && (Transfer != true || !string.IsNullOrEmpty(TransferId));
}

/// <summary>
/// Sent by the client to log off the Windows session on the server. The agent answers
/// with a <see cref="LogoffResultMessage"/> before the session closes.
/// </summary>
/// <param name="Id">Identifier of the request, repeated by its results.</param>
public sealed record LogoffMessage(string Id) : Message(MessageTypes.Logoff)
{
    internal override bool IsValid() => !string.IsNullOrEmpty(Id);
}

/// <summary>
/// Sent by the agent in answer to a <see cref="LogoffMessage"/>: once when the log off is
/// accepted, then again with <see cref="Accepted"/> false when Windows refused it.
/// </summary>
/// <param name="Id">Identifier of the request.</param>
/// <param name="Accepted">True when the session is about to log off.</param>
/// <param name="Message">Human readable text, in the language of the client.</param>
public sealed record LogoffResultMessage(string Id, bool Accepted, string? Message = null) : Message(MessageTypes.LogoffResult)
{
    internal override bool IsValid() => !string.IsNullOrEmpty(Id);
}

/// <summary>
/// Sent by the agent once a drop is finished.
/// </summary>
/// <param name="Id">Identifier of the drop.</param>
/// <param name="Status">Outcome of the drop.</param>
/// <param name="Message">Human readable text for the status bar.</param>
/// <param name="Effect">What the drop did inside the session.</param>
/// <param name="TargetWindow">Window that received the drop, when known.</param>
public sealed record DropResultMessage(
    string Id,
    DropStatus Status,
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
/// What a drop did inside the session. Written as <c>"copy"</c>, <c>"link"</c> or
/// <c>"none"</c>.
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
}

/// <summary>
/// Outcome of a drop. Written as <c>"ok"</c>, <c>"error"</c> or <c>"cancelled"</c>.
/// </summary>
public enum DropStatus
{
    /// <summary>
    /// The drop ended normally: a window accepted it, or nothing under the cursor did
    /// (<see cref="DropEffect.None"/>).
    /// </summary>
    [JsonStringEnumMemberName("ok")]
    Ok,

    /// <summary>The drop failed; the message says why.</summary>
    [JsonStringEnumMemberName("error")]
    Error,

    /// <summary>The drag was cancelled before the drop was delivered.</summary>
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
