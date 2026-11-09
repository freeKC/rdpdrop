namespace RdpDrop.Core.Paths;

/// <summary>
/// Why a client path cannot be mapped to the redirected drives.
/// </summary>
public enum PathRejection
{
    /// <summary>The path can be mapped.</summary>
    None,

    /// <summary>The path is empty or white space.</summary>
    Empty,

    /// <summary>A device path such as <c>\\.\C:\x</c>.</summary>
    DevicePath,

    /// <summary>A network (UNC) path such as <c>\\server\share\x</c>.</summary>
    NetworkPath,

    /// <summary>A path that does not start with a drive letter.</summary>
    RelativePath,

    /// <summary>A path such as <c>C:x</c>, relative to the current folder of a drive.</summary>
    DriveRelativePath,

    /// <summary>A path with a <c>.</c> or <c>..</c> segment.</summary>
    DotSegment,

    /// <summary>A path with characters that are not allowed in a file name.</summary>
    InvalidCharacters,
}

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
    /// <param name="reason">Why the path was rejected, in English, or null on success.</param>
    /// <returns>True when the path was mapped.</returns>
    public static bool TryMapToTsClient(string clientPath, out string mappedPath, out string? reason)
    {
        var rejection = Classify(clientPath, out mappedPath);
        reason = rejection == PathRejection.None ? null : Describe(rejection, clientPath);
        return rejection == PathRejection.None;
    }

    /// <summary>
    /// Same rules as <see cref="TryMapToTsClient"/>, with the reason given as a
    /// <see cref="PathRejection"/>, so that the caller can word it in the language of the
    /// user. Returns <see cref="PathRejection.None"/> when the path was mapped.
    /// </summary>
    /// <param name="clientPath">Path on the client computer.</param>
    /// <param name="mappedPath">The mapped path, or an empty string on failure.</param>
    public static PathRejection Classify(string? clientPath, out string mappedPath)
    {
        mappedPath = string.Empty;
        var rejection = Check(clientPath, out var drive, out var segments);
        if (rejection == PathRejection.None)
        {
            mappedPath = TsClientRoot + drive + @"\" + string.Join('\\', segments);
        }

        return rejection;
    }

    /// <summary>
    /// English text for a rejection of <paramref name="clientPath"/>, as used in the logs.
    /// Returns an empty string for <see cref="PathRejection.None"/>.
    /// </summary>
    public static string Describe(PathRejection rejection, string? clientPath) => rejection switch
    {
        PathRejection.None => string.Empty,
        PathRejection.Empty => "The path is empty.",
        PathRejection.DevicePath => $"\"{clientPath}\" is a device path. Only files on a local drive letter can be dropped.",
        PathRejection.NetworkPath => $"\"{clientPath}\" is a network (UNC) path. Only files on a local drive letter can be dropped, because the remote session reaches them through drive redirection.",
        PathRejection.RelativePath => $"\"{clientPath}\" is a relative path. Only full paths that start with a drive letter, such as C:\\, can be dropped.",
        PathRejection.DriveRelativePath => DriveRelativeReason(clientPath),
        PathRejection.DotSegment => $"\"{clientPath}\" contains a \".\" or \"..\" segment. Only normalized full paths can be dropped.",
        _ => $"\"{clientPath}\" contains characters that are not allowed in a file name.",
    };

    private static string DriveRelativeReason(string? clientPath)
    {
        var path = clientPath ?? string.Empty;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith("//?/", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        var letter = path.Length > 0 ? char.ToUpperInvariant(path[0]) : '?';
        return $"\"{clientPath}\" is relative to the current folder of drive {letter}:. Only full paths such as {letter}:\\folder can be dropped.";
    }

    private static PathRejection Check(string? path, out char drive, out string[] segments)
    {
        drive = '\0';
        segments = [];

        if (string.IsNullOrWhiteSpace(path))
        {
            return PathRejection.Empty;
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith("//?/", StringComparison.Ordinal))
        {
            path = path[4..];
            if (path.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return PathRejection.NetworkPath;
            }
        }
        else if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith("//./", StringComparison.Ordinal))
        {
            return PathRejection.DevicePath;
        }
        else if (path.Length > 1 && IsSeparator(path[0]) && IsSeparator(path[1]))
        {
            return PathRejection.NetworkPath;
        }

        if (path.Length < 2 || !char.IsAsciiLetter(path[0]) || path[1] != ':')
        {
            return PathRejection.RelativePath;
        }

        if (path.Length == 2 || !IsSeparator(path[2]))
        {
            return PathRejection.DriveRelativePath;
        }

        var parts = path[3..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part is "." or "..")
            {
                return PathRejection.DotSegment;
            }

            if (part.IndexOfAny(InvalidNameChars) >= 0 || part.Any(char.IsControl))
            {
                return PathRejection.InvalidCharacters;
            }
        }

        drive = char.ToUpperInvariant(path[0]);
        segments = parts;
        return PathRejection.None;
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
