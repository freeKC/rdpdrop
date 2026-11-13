namespace UltimateRDP.Core.RemoteApp;

/// <summary>Whether the main window of the client shows when it opens a file.</summary>
public enum LaunchWindow
{
    /// <summary>Hidden for a RemoteApp program, shown for a desktop.</summary>
    Auto,

    /// <summary><c>--show</c>: always shown.</summary>
    Show,

    /// <summary><c>--hidden</c>: never shown at the start, also for a desktop.</summary>
    Hidden,
}

/// <summary>
/// The command line of the client since 0.9.0:
/// <c>UltimateRDP.exe [--show | --hidden] [--ask-credentials] [file.rdp]</c>. Options and the
/// file come in any order; options are compared without case.
/// </summary>
/// <param name="Path">The .rdp file, or null.</param>
/// <param name="Window">What <c>--show</c> or <c>--hidden</c> asked for.</param>
/// <param name="AskCredentials">True with <c>--ask-credentials</c>.</param>
/// <param name="Error">Why the command line is refused, in English, or null.</param>
public sealed record LaunchOptions(string? Path, LaunchWindow Window, bool AskCredentials, string? Error)
{
    /// <summary>The option that shows the main window.</summary>
    public const string ShowOption = "--show";

    /// <summary>The option that keeps the main window hidden.</summary>
    public const string HiddenOption = "--hidden";

    /// <summary>The option that asks for the password at every connection.</summary>
    public const string AskCredentialsOption = "--ask-credentials";

    /// <summary>Reads the arguments of the command line.</summary>
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? path = null;
        var window = LaunchWindow.Auto;
        var ask = false;
        foreach (var raw in args)
        {
            var arg = raw.Trim();
            if (arg.Length == 0)
            {
                continue;
            }

            if (arg.Equals(ShowOption, StringComparison.OrdinalIgnoreCase) || arg.Equals(HiddenOption, StringComparison.OrdinalIgnoreCase))
            {
                var asked = arg.Equals(ShowOption, StringComparison.OrdinalIgnoreCase) ? LaunchWindow.Show : LaunchWindow.Hidden;
                if (window != LaunchWindow.Auto && window != asked)
                {
                    return new LaunchOptions(null, LaunchWindow.Auto, false, $"{ShowOption} and {HiddenOption} cannot be used together.");
                }

                window = asked;
                continue;
            }

            if (arg.Equals(AskCredentialsOption, StringComparison.OrdinalIgnoreCase))
            {
                ask = true;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                return new LaunchOptions(null, LaunchWindow.Auto, false, $"Unknown option {arg}.");
            }

            if (path is not null)
            {
                return new LaunchOptions(null, LaunchWindow.Auto, false, "Only one .rdp file can be given.");
            }

            path = arg;
        }

        return new LaunchOptions(path, window, ask, null);
    }

    /// <summary>
    /// True when the main window stays hidden for a file that runs a RemoteApp program when
    /// <paramref name="remoteApp"/> is true, and a desktop otherwise.
    /// </summary>
    public bool StartsHidden(bool remoteApp) => Window switch
    {
        LaunchWindow.Show => false,
        LaunchWindow.Hidden => true,
        _ => remoteApp,
    };
}
