namespace RdpDrop.Core.Targets;

/// <summary>
/// Folder used when the cursor is not over a usable folder.
/// </summary>
public enum FallbackKind
{
    /// <summary>The desktop folder.</summary>
    Desktop,

    /// <summary>The downloads folder.</summary>
    Downloads,

    /// <summary>A folder chosen by the user.</summary>
    Custom,
}

/// <summary>
/// Configured fallback folder.
/// </summary>
/// <param name="Kind">Which folder to use.</param>
/// <param name="CustomPath">Folder used when <paramref name="Kind"/> is <see cref="FallbackKind.Custom"/>.</param>
public sealed record FallbackFolder(FallbackKind Kind, string? CustomPath = null)
{
    /// <summary>The default fallback: the desktop.</summary>
    public static FallbackFolder Default { get; } = new(FallbackKind.Desktop);
}
