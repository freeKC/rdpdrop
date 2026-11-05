namespace RdpDrop.Core.Paths;

/// <summary>
/// Maps a path on the client computer to the path that reaches it from the remote
/// session through drive redirection.
/// </summary>
public static class ClientPathMapper
{
    /// <summary>Root of the redirected drives inside the session.</summary>
    public const string TsClientRoot = @"\\tsclient\";

    private static readonly char[] Separators = ['\\', '/'];
    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '|', '?', '*'];

    /// <summary>
    /// Turns <c>C:\x\y</c> into <c>\\tsclient\C\x\y</c>.
    /// </summary>
    /// <remarks>
    /// The drive letter is uppercased, forward slashes become backslashes, repeated
    /// separators are merged and a trailing separator is removed (except for a drive
    /// root, which maps to <c>\\tsclient\C\</c>). A <c>\\?\C:\</c> prefix is accepted.
    /// Rejected, with a reason: empty paths, UNC paths, device paths, relative paths
    /// (including <c>C:x</c>), paths with <c>.</c> or <c>..</c> segments and paths with
    /// characters that are not allowed in a file name.
    /// </remarks>
    /// <param name="clientPath">Path on the client computer.</param>
    /// <param name="mappedPath">The mapped path, or an empty string on failure.</param>
    /// <param name="reason">Why the path was rejected, or null on success.</param>
    /// <returns>True when the path was mapped.</returns>
    public static bool TryMapToTsClient(string clientPath, out string mappedPath, out string? reason)
    {
        mappedPath = string.Empty;
        reason = Check(clientPath, out var drive, out var segments);
        if (reason is not null)
        {
            return false;
        }

        mappedPath = TsClientRoot + drive + @"\" + string.Join('\\', segments);
        return true;
    }

    private static string? Check(string? path, out char drive, out string[] segments)
    {
        drive = '\0';
        segments = [];

        if (string.IsNullOrWhiteSpace(path))
        {
            return "The path is empty.";
        }

        var original = path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith("//?/", StringComparison.Ordinal))
        {
            path = path[4..];
            if (path.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return UncReason(original);
            }
        }
        else if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith("//./", StringComparison.Ordinal))
        {
            return $"\"{original}\" is a device path. Only files on a local drive letter can be dropped.";
        }
        else if (path.Length > 1 && IsSeparator(path[0]) && IsSeparator(path[1]))
        {
            return UncReason(original);
        }

        if (path.Length < 2 || !char.IsAsciiLetter(path[0]) || path[1] != ':')
        {
            return $"\"{original}\" is a relative path. Only full paths that start with a drive letter, such as C:\\, can be dropped.";
        }

        var letter = char.ToUpperInvariant(path[0]);
        if (path.Length == 2 || !IsSeparator(path[2]))
        {
            return $"\"{original}\" is relative to the current folder of drive {letter}:. Only full paths such as {letter}:\\folder can be dropped.";
        }

        var parts = path[3..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part is "." or "..")
            {
                return $"\"{original}\" contains a \".\" or \"..\" segment. Only normalized full paths can be dropped.";
            }

            if (part.IndexOfAny(InvalidNameChars) >= 0 || part.Any(char.IsControl))
            {
                return $"\"{original}\" contains characters that are not allowed in a file name.";
            }
        }

        drive = letter;
        segments = parts;
        return null;
    }

    private static string UncReason(string path) =>
        $"\"{path}\" is a network (UNC) path. Only files on a local drive letter can be dropped, because the remote session reaches them through drive redirection.";

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
