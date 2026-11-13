using UltimateRDP.Core.Launchers;

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
/// The command line of the client: <c>UltimateRDP.exe [options] [file.rdp]</c>. Options and
/// the file come in any order; options are compared without case. Since 0.10.0 the options
/// of a connection (<see cref="ConnectionOptions"/>) open a session without a file, with
/// <c>--server</c>, or go on top of the file: they win over its keys. An option that takes a
/// value takes the next argument; that argument cannot start with <c>--</c>, except the
/// arguments of a RemoteApp program.
/// </summary>
/// <param name="Path">The .rdp file, or null.</param>
/// <param name="Window">What <c>--show</c> or <c>--hidden</c> asked for.</param>
/// <param name="AskCredentials">True with <c>--ask-credentials</c>.</param>
/// <param name="Error">Why the command line is refused, or null.</param>
public sealed record LaunchOptions(string? Path, LaunchWindow Window, bool AskCredentials, LaunchError? Error)
{
    /// <summary>The option that shows the main window.</summary>
    public const string ShowOption = LaunchOptionNames.Show;

    /// <summary>The option that keeps the main window hidden.</summary>
    public const string HiddenOption = LaunchOptionNames.Hidden;

    /// <summary>The option that asks for the password at every connection.</summary>
    public const string AskCredentialsOption = LaunchOptionNames.AskCredentials;

    /// <summary>The options of a connection; <see cref="ConnectionOptions.None"/> without any.</summary>
    public ConnectionOptions Connection { get; init; } = ConnectionOptions.None;

    /// <summary>True with <c>--help</c> or <c>/?</c>: the client shows its options and opens nothing.</summary>
    public bool Help { get; init; }

    /// <summary>True when there is something to connect to: a file or <c>--server</c>.</summary>
    public bool HasConnection => Path is not null || Connection.Server is not null;

    /// <summary>Reads the arguments of the command line.</summary>
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? path = null;
        var window = LaunchWindow.Auto;
        var ask = false;
        var help = false;
        var connection = ConnectionOptions.None;
        var given = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i].Trim();
            if (arg.Length == 0)
            {
                continue;
            }

            if (arg.Equals(ShowOption, StringComparison.OrdinalIgnoreCase) || arg.Equals(HiddenOption, StringComparison.OrdinalIgnoreCase))
            {
                var asked = arg.Equals(ShowOption, StringComparison.OrdinalIgnoreCase) ? LaunchWindow.Show : LaunchWindow.Hidden;
                if (window != LaunchWindow.Auto && window != asked)
                {
                    return Refused(new LaunchError(LaunchErrorKind.ShowAndHidden));
                }

                window = asked;
                continue;
            }

            if (arg.Equals(AskCredentialsOption, StringComparison.OrdinalIgnoreCase))
            {
                ask = true;
                continue;
            }

            if (arg.Equals(LaunchOptionNames.Help, StringComparison.OrdinalIgnoreCase) || arg == LaunchOptionNames.HelpShort)
            {
                help = true;
                continue;
            }

            if (LaunchOptionNames.PasswordOptions.Contains(arg.Split('=')[0], StringComparer.OrdinalIgnoreCase))
            {
                return Refused(new LaunchError(LaunchErrorKind.Password, arg.Split('=')[0]));
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var option = arg.ToLowerInvariant();
                if (!ValueOptions.Contains(option))
                {
                    return Refused(new LaunchError(LaunchErrorKind.UnknownOption, arg));
                }

                var value = i + 1 < args.Count ? args[i + 1].Trim() : null;
                if (value is null || (option != LaunchOptionNames.RemoteAppArgs && value.StartsWith("--", StringComparison.Ordinal)))
                {
                    return Refused(new LaunchError(LaunchErrorKind.MissingValue, option));
                }

                if (!given.Add(option))
                {
                    return Refused(new LaunchError(LaunchErrorKind.Repeated, option));
                }

                i++;
                if (With(connection, option, value) is not { } next)
                {
                    return Refused(new LaunchError(LaunchErrorKind.InvalidValue, option, value, RuleOf(option)));
                }

                connection = next;
                continue;
            }

            if (path is not null)
            {
                return Refused(new LaunchError(LaunchErrorKind.TwoFiles));
            }

            path = arg;
        }

        if (help)
        {
            return new LaunchOptions(null, LaunchWindow.Auto, false, null) { Help = true };
        }

        if (connection.Check(hasFile: path is not null) is { } error)
        {
            return Refused(error);
        }

        return new LaunchOptions(path, window, ask, null) { Connection = connection };
    }

    /// <summary>
    /// The arguments that give these options back to <see cref="Parse"/>: the window, the
    /// password prompt, the options of the connection in a fixed order, then the file.
    /// </summary>
    public IReadOnlyList<string> ToArguments()
    {
        var args = new List<string>();
        if (Window != LaunchWindow.Auto)
        {
            args.Add(Window == LaunchWindow.Show ? ShowOption : HiddenOption);
        }

        if (AskCredentials)
        {
            args.Add(AskCredentialsOption);
        }

        foreach (var (option, value) in ConnectionArguments(Connection))
        {
            args.Add(option);
            args.Add(value);
        }

        if (Path is not null)
        {
            args.Add(Path);
        }

        return args;
    }

    /// <summary>The options of <paramref name="connection"/> with their values, in the order of the help.</summary>
    public static IReadOnlyList<(string Option, string Value)> ConnectionArguments(ConnectionOptions connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var list = new List<(string, string)>();
        void Add(string option, string? value)
        {
            if (value is not null)
            {
                list.Add((option, value));
            }
        }

        static string? Number(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        static string? Switch(bool? value) => value is bool on ? LaunchValues.OnOff(on) : null;
        Add(LaunchOptionNames.Server, connection.Server);
        Add(LaunchOptionNames.Port, Number(connection.Port));
        Add(LaunchOptionNames.User, connection.User);
        Add(LaunchOptionNames.Domain, connection.Domain);
        Add(LaunchOptionNames.RemoteApp, connection.RemoteApp);
        Add(LaunchOptionNames.RemoteAppName, connection.RemoteAppName);
        Add(LaunchOptionNames.RemoteAppArgs, connection.RemoteAppArgs);
        Add(LaunchOptionNames.Gateway, connection.Gateway);
        Add(LaunchOptionNames.Multimon, Switch(connection.Multimon));
        Add(LaunchOptionNames.Scale, Number(connection.Scale));
        Add(LaunchOptionNames.WindowsKeys, connection.WindowsKeys?.ToString().ToLowerInvariant());
        Add(LaunchOptionNames.Clipboard, Switch(connection.Clipboard));
        Add(LaunchOptionNames.Drives, Switch(connection.Drives));
        Add(LaunchOptionNames.Printers, Switch(connection.Printers));
        Add(LaunchOptionNames.Audio, connection.Audio?.ToString().ToLowerInvariant());
        return list;
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

    private static readonly HashSet<string> ValueOptions =
    [
        LaunchOptionNames.Server, LaunchOptionNames.Port, LaunchOptionNames.User, LaunchOptionNames.Domain,
        LaunchOptionNames.RemoteApp, LaunchOptionNames.RemoteAppName, LaunchOptionNames.RemoteAppArgs,
        LaunchOptionNames.Gateway, LaunchOptionNames.Multimon, LaunchOptionNames.Scale, LaunchOptionNames.WindowsKeys,
        LaunchOptionNames.Clipboard, LaunchOptionNames.Drives, LaunchOptionNames.Printers, LaunchOptionNames.Audio,
    ];

    private static LaunchOptions Refused(LaunchError error) => new(null, LaunchWindow.Auto, false, error);

    /// <summary><paramref name="connection"/> with the value of <paramref name="option"/>, or null when the value does not follow its rule.</summary>
    private static ConnectionOptions? With(ConnectionOptions connection, string option, string value)
    {
        var next = option switch
        {
            LaunchOptionNames.Server => connection with { Server = value },
            LaunchOptionNames.Port => LaunchValues.ParseNumber(value) is int port ? connection with { Port = port } : null,
            LaunchOptionNames.User => connection with { User = value },
            LaunchOptionNames.Domain => connection with { Domain = value },
            LaunchOptionNames.RemoteApp => connection with { RemoteApp = value },
            LaunchOptionNames.RemoteAppName => connection with { RemoteAppName = value },
            LaunchOptionNames.RemoteAppArgs => connection with { RemoteAppArgs = value },
            LaunchOptionNames.Gateway => connection with { Gateway = value },
            LaunchOptionNames.Multimon => LaunchValues.ParseOnOff(value) is bool on ? connection with { Multimon = on } : null,
            LaunchOptionNames.Scale => LaunchValues.ParseNumber(value) is int scale ? connection with { Scale = scale } : null,
            LaunchOptionNames.WindowsKeys => ParseEnum<WindowsKeysTarget>(value) is { } keys ? connection with { WindowsKeys = keys } : null,
            LaunchOptionNames.Clipboard => LaunchValues.ParseOnOff(value) is bool on ? connection with { Clipboard = on } : null,
            LaunchOptionNames.Drives => LaunchValues.ParseOnOff(value) is bool on ? connection with { Drives = on } : null,
            LaunchOptionNames.Printers => LaunchValues.ParseOnOff(value) is bool on ? connection with { Printers = on } : null,
            LaunchOptionNames.Audio => ParseEnum<AudioTarget>(value) is { } audio ? connection with { Audio = audio } : null,
            _ => null,
        };

        // The rules of each value, checked here so that the error names the option given.
        return next?.Check(hasFile: true) is null ? next : null;
    }

    private static TEnum? ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(v => (TEnum?)v).FirstOrDefault(v => v.ToString()!.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static LaunchValueRule RuleOf(string option) => option switch
    {
        LaunchOptionNames.Server or LaunchOptionNames.Gateway => LaunchValueRule.Host,
        LaunchOptionNames.Port => LaunchValueRule.Port,
        LaunchOptionNames.RemoteApp => LaunchValueRule.Program,
        LaunchOptionNames.RemoteAppArgs => LaunchValueRule.Arguments,
        LaunchOptionNames.Scale => LaunchValueRule.Scale,
        LaunchOptionNames.WindowsKeys => LaunchValueRule.WindowsKeys,
        LaunchOptionNames.Audio => LaunchValueRule.Audio,
        LaunchOptionNames.Multimon or LaunchOptionNames.Clipboard or LaunchOptionNames.Drives or LaunchOptionNames.Printers => LaunchValueRule.OnOff,
        _ => LaunchValueRule.Name,
    };
}
