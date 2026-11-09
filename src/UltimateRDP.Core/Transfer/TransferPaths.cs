namespace UltimateRDP.Core.Transfer;

/// <summary>A dropped item as the transfer sees it: the name of its copy, and whether it is a folder.</summary>
/// <param name="Name">Name of the copy, see <see cref="TransferPaths.ItemName"/>.</param>
/// <param name="IsDirectory">True for a folder, whose files arrive under <c>Name\</c>.</param>
public sealed record TransferItem(string Name, bool IsDirectory);

/// <summary>
/// Names and relative paths of the files of a transfer. The client builds them; the agent
/// checks them before it writes anything, so that a path can never leave the folder of the
/// transfer.
/// </summary>
public static class TransferPaths
{
    /// <summary>Separator of the segments of a relative path.</summary>
    public const char Separator = '\\';

    /// <summary>Longest relative path accepted, in characters.</summary>
    public const int MaxLength = 4096;

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Name of the copy of a dropped item: the last segment of its path, or "Drive C" for
    /// the root of drive C. The same rule names the staged copies.
    /// </summary>
    public static string ItemName(string clientPath)
    {
        ArgumentNullException.ThrowIfNull(clientPath);
        var segments = clientPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return string.Empty;
        }

        var last = segments[^1];
        if (segments.Length == 1 && last.Length == 2 && last[1] == ':' && char.IsAsciiLetter(last[0]))
        {
            return "Drive " + char.ToUpperInvariant(last[0]);
        }

        return last;
    }

    /// <summary>The relative path of a file inside a dropped folder.</summary>
    /// <param name="itemName">Name of the dropped folder.</param>
    /// <param name="pathInFolder">Path of the file below the folder, with <c>\</c> or <c>/</c> between segments.</param>
    public static string Combine(string itemName, string pathInFolder)
    {
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(pathInFolder);
        return itemName + Separator + pathInFolder.Replace('/', Separator).Trim(Separator);
    }

    /// <summary>
    /// True when <paramref name="relativePath"/> may be written inside the folder of a
    /// transfer: segments separated by <c>\</c>, none empty, none <c>.</c> or <c>..</c>,
    /// no character that Windows refuses in a name, no name that ends with a dot or a
    /// space, no device name such as NUL, and a first segment that names one of
    /// <paramref name="items"/>: the file itself, or a folder that holds the file.
    /// </summary>
    public static bool IsAllowed(string? relativePath, IReadOnlyList<TransferItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (string.IsNullOrEmpty(relativePath) || relativePath.Length > MaxLength)
        {
            return false;
        }

        var segments = relativePath.Split(Separator);
        foreach (var segment in segments)
        {
            if (!IsValidName(segment))
            {
                return false;
            }
        }

        foreach (var item in items)
        {
            if (string.Equals(item.Name, segments[0], StringComparison.OrdinalIgnoreCase))
            {
                return item.IsDirectory ? segments.Length > 1 : segments.Length == 1;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="name"/> can be the name of a file or folder on Windows.</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Length > 255)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c < 0x20 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                return false;
            }
        }

        if (name[^1] is '.' or ' ')
        {
            return false;
        }

        var dot = name.IndexOf('.');
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        return !ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }
}
