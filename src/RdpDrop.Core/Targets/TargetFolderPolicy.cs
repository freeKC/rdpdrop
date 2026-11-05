namespace RdpDrop.Core.Targets;

/// <summary>
/// Chooses the folder that receives a drop.
/// </summary>
public static class TargetFolderPolicy
{
    /// <summary>
    /// Returns the folder that receives the drop.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>An Explorer window showing a file system folder gives that folder.</item>
    /// <item>The desktop gives <paramref name="desktopPath"/>.</item>
    /// <item>
    /// Anything else (a virtual folder, an Explorer window without a file system path,
    /// another window) gives the fallback: <paramref name="desktopPath"/>,
    /// <paramref name="downloadsPath"/> or the custom path. A custom fallback without a
    /// file system path gives <paramref name="desktopPath"/>.
    /// </item>
    /// </list>
    /// No check is made that the folder exists; the caller does that.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="desktopPath"/> or <paramref name="downloadsPath"/> is empty.</exception>
    public static string Resolve(CursorTarget target, FallbackFolder fallback, string desktopPath, string downloadsPath)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadsPath);

        return target.Kind switch
        {
            CursorTargetKind.ExplorerFolder when IsFileSystemPath(target.FolderPath) => target.FolderPath!,
            CursorTargetKind.Desktop => desktopPath,
            _ => ResolveFallback(fallback, desktopPath, downloadsPath),
        };
    }

    /// <summary>
    /// True for a full path that starts with a drive letter (<c>C:\</c>) or a UNC share
    /// (<c>\\server\share</c>). False for shell names such as <c>::{GUID}</c>, device
    /// paths, relative paths and empty values.
    /// </summary>
    public static bool IsFileSystemPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        {
            return true;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) && path.Length > 2 && path[2] is not ('?' or '.' or '\\'))
        {
            // \\server\share: a server name followed by a share name.
            var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2;
        }

        return false;
    }

    private static string ResolveFallback(FallbackFolder fallback, string desktopPath, string downloadsPath) =>
        fallback.Kind switch
        {
            FallbackKind.Downloads => downloadsPath,
            FallbackKind.Custom when IsFileSystemPath(fallback.CustomPath) => fallback.CustomPath!,
            _ => desktopPath,
        };
}
