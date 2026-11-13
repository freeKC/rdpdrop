namespace UltimateRDP.Core.Agent;

/// <summary>Whether the agent maps the redirected drives of the client to drive letters of the session.</summary>
public enum MapClientDrivesMode
{
    /// <summary>Never. The drives stay reachable as <c>\\tsclient\C</c> and so on. The default.</summary>
    Never,

    /// <summary>While an UltimateRDP client is connected, each redirected drive gets a letter of the session.</summary>
    Auto,
}

/// <summary>One redirected drive of the client and the letter it gets in the session.</summary>
/// <param name="ClientDrive">The letter of the drive on the client, as in <c>\\tsclient\C</c>.</param>
/// <param name="Letter">The letter it gets in the session.</param>
public readonly record struct ClientDriveLetter(char ClientDrive, char Letter)
{
    /// <summary>The share of the drive in the session, such as <c>\\tsclient\C</c>.</summary>
    public string RemoteName => @"\\tsclient\" + ClientDrive;

    /// <summary>The local name of the letter, such as <c>Z:</c>.</summary>
    public string LocalName => Letter + ":";
}

/// <summary>
/// Reads and writes the <c>mapClientDrives</c> setting of agent.json, and chooses the letters
/// of the redirected drives of the client in the session.
/// </summary>
public static class ClientDriveLetters
{
    /// <summary>
    /// Reads a setting value: "never" or "auto", in any case and with spaces around. A missing
    /// value gives <see cref="MapClientDrivesMode.Never"/> and counts as recognized; any other
    /// value gives <see cref="MapClientDrivesMode.Never"/> and <paramref name="recognized"/>
    /// false, so that the caller can log it.
    /// </summary>
    public static MapClientDrivesMode Parse(string? value, out bool recognized)
    {
        recognized = true;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "never":
                return MapClientDrivesMode.Never;

            case "auto":
                return MapClientDrivesMode.Auto;

            default:
                recognized = false;
                return MapClientDrivesMode.Never;
        }
    }

    /// <summary>The value written to agent.json: "never" or "auto".</summary>
    public static string ToSetting(MapClientDrivesMode mode) => mode == MapClientDrivesMode.Auto ? "auto" : "never";

    /// <summary>
    /// The letters of the redirected drives <paramref name="clientDrives"/> in a session where
    /// <paramref name="taken"/> are in use: local drives, existing mappings and letters kept
    /// for something else. A drive keeps its own letter when that letter is free in the
    /// session; the others get the free letters from Z down to C, in the order of the drives.
    /// A, B and anything that is not a letter are skipped, on both sides, and never replace
    /// anything. Each client drive and each letter appears once. The drives that find no
    /// letter are in <paramref name="unmapped"/>.
    /// </summary>
    public static IReadOnlyList<ClientDriveLetter> Choose(IEnumerable<char> clientDrives, IEnumerable<char> taken, out IReadOnlyList<char> unmapped)
    {
        ArgumentNullException.ThrowIfNull(clientDrives);
        ArgumentNullException.ThrowIfNull(taken);
        var drives = clientDrives.Select(char.ToUpperInvariant).Where(IsMappable).Distinct().ToList();
        var used = new HashSet<char>(taken.Select(char.ToUpperInvariant));
        var letters = new Dictionary<char, char>();

        // First the drives whose own letter is free, so that a later drive never loses its
        // letter to an earlier one that had to move.
        foreach (var drive in drives)
        {
            if (used.Add(drive))
            {
                letters[drive] = drive;
            }
        }

        var missing = new List<char>();
        foreach (var drive in drives.Where(drive => !letters.ContainsKey(drive)))
        {
            var free = FreeLettersFromZ().FirstOrDefault(letter => !used.Contains(letter));
            if (free == default)
            {
                missing.Add(drive);
                continue;
            }

            used.Add(free);
            letters[drive] = free;
        }

        unmapped = missing;
        return [.. drives.Where(letters.ContainsKey).Select(drive => new ClientDriveLetter(drive, letters[drive]))];
    }

    /// <summary>A letter a drive may have or get: C to Z.</summary>
    public static bool IsMappable(char letter) => letter is >= 'C' and <= 'Z';

    private static IEnumerable<char> FreeLettersFromZ()
    {
        for (var letter = 'Z'; letter >= 'C'; letter--)
        {
            yield return letter;
        }
    }
}
