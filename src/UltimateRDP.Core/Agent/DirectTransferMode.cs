namespace UltimateRDP.Core.Agent;

/// <summary>Whether a fast transfer may use the direct connection, outside the RDP channel.</summary>
public enum DirectTransferMode
{
    /// <summary>The direct connection is offered, and the channel is the fallback. The default.</summary>
    Auto,

    /// <summary>Never: the files of a fast transfer always go through the channel.</summary>
    Never,
}

/// <summary>
/// Reads and writes the <c>directTransfer</c> setting of agent.json and client.json, and the
/// <c>directPortRange</c> setting of agent.json.
/// </summary>
public static class DirectTransferPolicy
{
    /// <summary>First port of the default range.</summary>
    public const int DefaultFirstPort = 3400;

    /// <summary>Last port of the default range.</summary>
    public const int DefaultLastPort = 3449;

    /// <summary>The default range as the setting writes it.</summary>
    public const string DefaultPortRange = "3400-3449";

    /// <summary>A range starts at this port or above: the ports below need no privilege on Windows either, but are left to the system services.</summary>
    public const int MinPort = 1025;

    /// <summary>Most ports in a range.</summary>
    public const int MaxPorts = 1000;

    /// <summary>
    /// Reads a setting value: "auto" or "never", in any case and with spaces around. A missing
    /// value gives <see cref="DirectTransferMode.Auto"/> and counts as recognized; any other
    /// value gives <see cref="DirectTransferMode.Auto"/> and <paramref name="recognized"/>
    /// false, so that the caller can log it.
    /// </summary>
    public static DirectTransferMode Parse(string? value, out bool recognized)
    {
        recognized = true;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "auto":
                return DirectTransferMode.Auto;

            case "never":
                return DirectTransferMode.Never;

            default:
                recognized = false;
                return DirectTransferMode.Auto;
        }
    }

    /// <summary>The value written to the settings file: "auto" or "never".</summary>
    public static string ToSetting(DirectTransferMode mode) => mode == DirectTransferMode.Never ? "never" : "auto";

    /// <summary>
    /// Reads a port range such as "3400-3449", or a single port such as "3400", with spaces
    /// allowed around the numbers. The ports must be from <see cref="MinPort"/> to 65535, the
    /// first not above the last, and at most <see cref="MaxPorts"/> of them. A missing value
    /// gives the default range and counts as recognized; anything else gives the default range
    /// and <paramref name="recognized"/> false.
    /// </summary>
    public static (int First, int Last) ParsePortRange(string? value, out bool recognized)
    {
        recognized = true;
        if (string.IsNullOrWhiteSpace(value))
        {
            return (DefaultFirstPort, DefaultLastPort);
        }

        var parts = value.Split('-');
        if (parts.Length is 1 or 2
            && int.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var first)
            && int.TryParse(parts[^1].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var last)
            && first >= MinPort && last <= 65535 && first <= last && last - first < MaxPorts)
        {
            return (first, last);
        }

        recognized = false;
        return (DefaultFirstPort, DefaultLastPort);
    }

    /// <summary>The range as the setting writes it: "3400-3449", or "3400" for a single port.</summary>
    public static string ToSetting((int First, int Last) range) =>
        range.First == range.Last
            ? range.First.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{range.First}-{range.Last}");
}
