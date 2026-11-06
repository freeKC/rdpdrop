namespace RdpDrop.Core.Recent;

/// <summary>
/// Rules of the list of recently opened .rdp files: most recent first, at most
/// <see cref="MaxCount"/> entries, no blank entry, and no path twice. Paths are compared
/// as full paths without regard to case, like Windows does.
/// </summary>
/// <remarks>
/// Every method returns a new list and leaves its input untouched.
/// </remarks>
public static class RecentList
{
    /// <summary>Largest number of entries kept.</summary>
    public const int MaxCount = 10;

    /// <summary>Comparer used to tell whether two entries are the same file.</summary>
    public static StringComparer PathComparer { get; } = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Returns <paramref name="current"/> with <paramref name="path"/> moved or added at
    /// the top, trimmed to <see cref="MaxCount"/> entries. An earlier entry for the same
    /// file is removed; the new spelling of the path is kept.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public static IReadOnlyList<string> Add(IEnumerable<string?>? current, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var trimmed = path.Trim();
        return Normalize(Prepend(trimmed, current));
    }

    /// <summary>
    /// Returns <paramref name="current"/> without the entry for <paramref name="path"/>.
    /// </summary>
    public static IReadOnlyList<string> Remove(IEnumerable<string?>? current, string? path)
    {
        var trimmed = path?.Trim();
        return Normalize(current).Where(entry => !PathComparer.Equals(entry, trimmed)).ToList();
    }

    /// <summary>
    /// Returns <paramref name="current"/> without blank entries and without repeats (the
    /// first occurrence wins), trimmed to <see cref="MaxCount"/> entries. Used on a list
    /// read from the settings file, which the user may have edited.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? current)
    {
        var result = new List<string>(MaxCount);
        var seen = new HashSet<string>(PathComparer);
        foreach (var entry in current ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var trimmed = entry.Trim();
            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
                if (result.Count == MaxCount)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static IEnumerable<string?> Prepend(string first, IEnumerable<string?>? rest)
    {
        yield return first;
        foreach (var entry in rest ?? [])
        {
            yield return entry;
        }
    }
}
