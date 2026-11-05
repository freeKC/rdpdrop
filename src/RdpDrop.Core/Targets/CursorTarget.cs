namespace RdpDrop.Core.Targets;

/// <summary>
/// What the agent found under the cursor.
/// </summary>
public enum CursorTargetKind
{
    /// <summary>An Explorer window; <see cref="CursorTarget.FolderPath"/> holds the folder it shows.</summary>
    ExplorerFolder,

    /// <summary>The desktop.</summary>
    Desktop,

    /// <summary>An Explorer window showing a folder that is not on the file system, such as This PC.</summary>
    VirtualFolder,

    /// <summary>Any other window, or nothing.</summary>
    Other,
}

/// <summary>
/// Description of the window under the cursor.
/// </summary>
/// <param name="Kind">Kind of window.</param>
/// <param name="FolderPath">Folder shown by an Explorer window, when known.</param>
public sealed record CursorTarget(CursorTargetKind Kind, string? FolderPath = null);
