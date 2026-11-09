namespace UltimateRDP.Client.Rdp;

/// <summary>
/// The drives named by <c>drivestoredirect</c>, for example <c>*</c>, <c>C:\;D:\;</c>
/// or <c>DynamicDrives</c>.
/// </summary>
internal sealed record DriveSelection(bool AllDrives, bool DynamicDrives, IReadOnlySet<char> Letters)
{
    public static DriveSelection Parse(string? drivesToRedirect)
    {
        if (string.IsNullOrWhiteSpace(drivesToRedirect))
        {
            return new DriveSelection(true, false, new HashSet<char>());
        }

        var all = false;
        var dynamic = false;
        var letters = new HashSet<char>();
        foreach (var raw in drivesToRedirect.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*")
            {
                all = true;
                dynamic = true;
            }
            else if (raw.Equals("DynamicDrives", StringComparison.OrdinalIgnoreCase))
            {
                dynamic = true;
            }
            else if (raw.Length >= 2 && char.IsAsciiLetter(raw[0]) && raw[1] == ':')
            {
                letters.Add(char.ToUpperInvariant(raw[0]));
            }
        }

        if (!all && letters.Count == 0 && !dynamic)
        {
            all = true;
        }

        return new DriveSelection(all, dynamic, letters);
    }

    /// <summary>
    /// True when the drive whose name starts with a letter should be redirected.
    /// </summary>
    public bool Includes(string driveName) =>
        AllDrives || (driveName.Length > 0 && Letters.Contains(char.ToUpperInvariant(driveName[0])));
}
