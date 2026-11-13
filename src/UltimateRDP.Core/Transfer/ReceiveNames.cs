using System.Globalization;

namespace UltimateRDP.Core.Transfer;

/// <summary>
/// The names of the files that the client receives from the session, since 0.9.0: the hidden
/// folder they arrive in, and the name each item takes in the folder for received files,
/// which never replaces an existing file or folder.
/// </summary>
public static class ReceiveNames
{
    /// <summary>Most numbers tried for a free name before giving up.</summary>
    public const int MaxAttempts = 10_000;

    /// <summary>
    /// The hidden folder of a transfer in the folder for received files,
    /// <c>.ultimaterdp-&lt;id&gt;.part</c>, or null when the id is not made of 1 to 64
    /// letters and digits, which the agent always sends.
    /// </summary>
    public static string? PartFolderName(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || !id.All(char.IsAsciiLetterOrDigit))
        {
            return null;
        }

        return ".ultimaterdp-" + id + ".part";
    }

    /// <summary>
    /// The name an item takes: <paramref name="name"/> itself when <paramref name="exists"/>
    /// says it is free, otherwise "name (2).ext", "name (3).ext" and so on for a file, and
    /// "name (2)" for a folder, whose files keep their names inside it. A name without a stem,
    /// such as ".config", gets the number at its end. Throws an <see cref="IOException"/> when
    /// <see cref="MaxAttempts"/> names are taken.
    /// </summary>
    public static string Unique(string name, bool isDirectory, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(exists);
        if (!exists(name))
        {
            return name;
        }

        var extension = isDirectory ? string.Empty : Path.GetExtension(name);
        var stem = name[..^extension.Length];
        if (stem.Length == 0)
        {
            stem = name;
            extension = string.Empty;
        }

        for (var number = 2; number <= MaxAttempts; number++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{stem} ({number}){extension}");
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"No free name for {name} after {MaxAttempts} attempts.");
    }
}
