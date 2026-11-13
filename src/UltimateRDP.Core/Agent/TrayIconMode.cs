namespace UltimateRDP.Core.Agent;

/// <summary>When the agent shows its icon in the notification area.</summary>
public enum TrayIconMode
{
    /// <summary>Only while an UltimateRDP client is connected to the session. The default.</summary>
    Auto,

    /// <summary>From the start of the agent until it exits.</summary>
    Always,

    /// <summary>Never. The agent is then controlled from the command line only.</summary>
    Never,
}

/// <summary>
/// Reads and writes the <c>trayIcon</c> setting of agent.json, and decides when the icon
/// is visible.
/// </summary>
public static class TrayIconModes
{
    /// <summary>
    /// Reads a setting value: "auto", "always" or "never", in any case and with spaces
    /// around. A missing value gives <see cref="TrayIconMode.Auto"/> and counts as
    /// recognized; any other value gives <see cref="TrayIconMode.Auto"/> and
    /// <paramref name="recognized"/> false, so that the caller can log it.
    /// </summary>
    public static TrayIconMode Parse(string? value, out bool recognized)
    {
        recognized = true;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "auto":
                return TrayIconMode.Auto;

            case "always":
                return TrayIconMode.Always;

            case "never":
                return TrayIconMode.Never;

            default:
                recognized = false;
                return TrayIconMode.Auto;
        }
    }

    /// <summary>The value written to agent.json: "auto", "always" or "never".</summary>
    public static string ToSetting(TrayIconMode mode) => mode switch
    {
        TrayIconMode.Always => "always",
        TrayIconMode.Never => "never",
        _ => "auto",
    };

    /// <summary>
    /// True when the icon must be visible, given whether an UltimateRDP client is
    /// connected to the session through the channel, and whether the session is a
    /// RemoteApp session. In a RemoteApp session, <see cref="TrayIconMode.Auto"/> never
    /// shows the icon: the server would show it on the client next to the program, and an
    /// icon may count as a reason to keep the session open.
    /// </summary>
    public static bool IsVisible(TrayIconMode mode, bool clientConnected, bool remoteAppSession = false) => mode switch
    {
        TrayIconMode.Always => true,
        TrayIconMode.Never => false,
        _ => clientConnected && !remoteAppSession,
    };
}
