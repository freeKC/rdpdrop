namespace UltimateRDP.Core.Launchers;

/// <summary>The options of the command line of the client. They are compared without case.</summary>
public static class LaunchOptionNames
{
    /// <summary>Shows the main window.</summary>
    public const string Show = "--show";

    /// <summary>Keeps the main window hidden.</summary>
    public const string Hidden = "--hidden";

    /// <summary>Asks for the password at every connection.</summary>
    public const string AskCredentials = "--ask-credentials";

    /// <summary>Shows the list of the options.</summary>
    public const string Help = "--help";

    /// <summary>Shows the list of the options, the Windows way.</summary>
    public const string HelpShort = "/?";

    /// <summary>The computer to connect to: a host name or an IP address.</summary>
    public const string Server = "--server";

    /// <summary>The port, 3389 when absent.</summary>
    public const string Port = "--port";

    /// <summary>The user name.</summary>
    public const string User = "--user";

    /// <summary>The domain of the user.</summary>
    public const string Domain = "--domain";

    /// <summary>The RemoteApp program: an alias or a path on the server.</summary>
    public const string RemoteApp = "--remoteapp";

    /// <summary>The name of the RemoteApp program shown to the user.</summary>
    public const string RemoteAppName = "--remoteapp-name";

    /// <summary>The arguments of the RemoteApp program.</summary>
    public const string RemoteAppArgs = "--remoteapp-args";

    /// <summary>The Remote Desktop Gateway.</summary>
    public const string Gateway = "--gateway";

    /// <summary>Every monitor in full screen, on or off.</summary>
    public const string Multimon = "--multimon";

    /// <summary>The scale factor of the remote desktop, in percent.</summary>
    public const string Scale = "--scale";

    /// <summary>Where the Windows key combinations go.</summary>
    public const string WindowsKeys = "--windows-keys";

    /// <summary>The shared clipboard, on or off.</summary>
    public const string Clipboard = "--clipboard";

    /// <summary>The drives of this computer in the session, on or off.</summary>
    public const string Drives = "--drives";

    /// <summary>The printers of this computer in the session, on or off.</summary>
    public const string Printers = "--printers";

    /// <summary>Where the sound plays.</summary>
    public const string Audio = "--audio";

    /// <summary>Options refused on purpose, with a message that says why: a password never goes on a command line.</summary>
    public static IReadOnlyList<string> PasswordOptions { get; } = ["--password", "--pass", "--pwd"];
}
