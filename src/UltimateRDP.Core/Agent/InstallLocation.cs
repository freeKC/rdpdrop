namespace UltimateRDP.Core.Agent;

/// <summary>
/// Rules on the folder of the agent executable when it is registered for every user.
/// </summary>
public static class InstallLocation
{
    /// <summary>The folder suggested for an installation for every user.</summary>
    public const string SuggestedFolder = @"C:\Program Files\UltimateRDP";

    /// <summary>
    /// True when <paramref name="executablePath"/> lies inside one of
    /// <paramref name="profileRoots"/>, for example the profile of the current user or the
    /// folder that holds every profile. Other users may not be able to read such a path,
    /// and a user can replace the file. Both sides must be full paths; the comparison
    /// ignores case and accepts both kinds of slash. Empty roots are skipped.
    /// </summary>
    public static bool IsUnderAny(string executablePath, IEnumerable<string?> profileRoots)
    {
        var path = Normalize(executablePath);
        if (path.Length == 0)
        {
            return false;
        }

        foreach (var root in profileRoots)
        {
            var folder = Normalize(root);
            if (folder.Length == 0)
            {
                continue;
            }

            if (path.Length > folder.Length
                && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                && path[folder.Length] == '\\')
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string? path) =>
        (path ?? string.Empty).Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
}
