using System.Globalization;
using UltimateRDP.Core.RdpFiles;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Core.Launchers;

/// <summary>A field of the launcher generator, to point at the one that is wrong.</summary>
public enum LauncherField
{
    /// <summary>The name of the launcher.</summary>
    Name,

    /// <summary>The server.</summary>
    Server,

    /// <summary>The port.</summary>
    Port,

    /// <summary>The user name.</summary>
    User,

    /// <summary>The domain.</summary>
    Domain,

    /// <summary>The RemoteApp program.</summary>
    Program,

    /// <summary>The name of the RemoteApp program.</summary>
    DisplayName,

    /// <summary>The arguments of the RemoteApp program.</summary>
    Arguments,

    /// <summary>The gateway.</summary>
    Gateway,

    /// <summary>The path of UltimateRDP.exe.</summary>
    Exe,
}

/// <summary>What is wrong in the fields of the launcher generator.</summary>
/// <param name="Field">The field at fault.</param>
/// <param name="Rule">What its value must be, or null when it is empty but needed.</param>
/// <param name="Problem">A problem of the launcher as a whole, such as a command line too long.</param>
public sealed record LauncherFieldError(LauncherField Field, LaunchValueRule? Rule, LauncherProblem Problem = LauncherProblem.None);

/// <summary>
/// The fields of the launcher generator as the user typed them, since 0.10.0. Texts are
/// trimmed; an empty text means "not given". <see cref="ToSpec"/> turns them into a
/// <see cref="LauncherSpec"/> or says which field is wrong.
/// </summary>
public sealed record LauncherDraft
{
    /// <summary>The name of the launcher, used for the comment and the file names; empty for <see cref="DefaultName"/>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The server, needed.</summary>
    public string Server { get; init; } = string.Empty;

    /// <summary>The port, 3389 when empty.</summary>
    public string Port { get; init; } = LaunchValues.DefaultPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>The user name, optional.</summary>
    public string User { get; init; } = string.Empty;

    /// <summary>The domain, optional.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>True for a RemoteApp program, false for the remote desktop.</summary>
    public bool RemoteApp { get; init; }

    /// <summary>The program, needed for a RemoteApp program.</summary>
    public string Program { get; init; } = string.Empty;

    /// <summary>The name of the program, optional.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The arguments of the program, optional.</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>The gateway, optional.</summary>
    public string Gateway { get; init; } = string.Empty;

    /// <summary>Asks for the password at every connection.</summary>
    public bool AskPassword { get; init; }

    /// <summary>The main window.</summary>
    public LaunchWindow Window { get; init; } = LaunchWindow.Auto;

    /// <summary>The display and redirection choices; null for the choice of the client.</summary>
    public bool? Multimon { get; init; }

    /// <inheritdoc cref="ConnectionOptions.Scale"/>
    public int? Scale { get; init; }

    /// <inheritdoc cref="ConnectionOptions.WindowsKeys"/>
    public WindowsKeysTarget? WindowsKeys { get; init; }

    /// <inheritdoc cref="ConnectionOptions.Clipboard"/>
    public bool? Clipboard { get; init; }

    /// <inheritdoc cref="ConnectionOptions.Drives"/>
    public bool? Drives { get; init; }

    /// <inheritdoc cref="ConnectionOptions.Printers"/>
    public bool? Printers { get; init; }

    /// <inheritdoc cref="ConnectionOptions.Audio"/>
    public AudioTarget? Audio { get; init; }

    /// <summary>Where the launcher finds UltimateRDP.exe.</summary>
    public LauncherExeKind ExeKind { get; init; } = LauncherExeKind.FullPath;

    /// <summary>The path typed for <see cref="LauncherExeKind.WithVariables"/>.</summary>
    public string TypedExe { get; init; } = string.Empty;

    /// <summary>A .bat and a .rdp file beside it, instead of one .bat with every option.</summary>
    public bool WithRdpFile { get; init; }

    /// <summary>
    /// The name when <see cref="Name"/> is empty: the name of the program, else its alias or
    /// file name, for a RemoteApp program; the server for a desktop; "UltimateRDP" without any.
    /// </summary>
    public string DefaultName
    {
        get
        {
            if (RemoteApp && DisplayName.Trim() is { Length: > 0 } display)
            {
                return display;
            }

            if (RemoteApp && Program.Trim() is { Length: > 0 } program)
            {
                return new RemoteAppLaunch(LaunchValues.RdpProgram(program), null, null, null, false, false).DisplayName;
            }

            return Server.Trim() is { Length: > 0 } server ? server : "UltimateRDP";
        }
    }

    /// <summary>The name of the launcher: <see cref="Name"/>, or <see cref="DefaultName"/>.</summary>
    public string EffectiveName => Name.Trim() is { Length: > 0 } name ? name : DefaultName;

    /// <summary>
    /// The name of the files, without extension: <see cref="EffectiveName"/> without the
    /// characters a file name or a launcher cannot hold (<c>\ / : * ? " &lt; &gt; | %</c> and
    /// control characters), without dots and spaces at its ends, at most 120 characters.
    /// </summary>
    public string FileBaseName
    {
        get
        {
            var refused = Path.GetInvalidFileNameChars().Concat(['%', '\\', '/', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
            var text = new string(EffectiveName.Where(c => !refused.Contains(c) && !char.IsControl(c) && !char.IsSurrogate(c)).ToArray()).Trim(' ', '.');
            if (text.Length > 120)
            {
                text = text[..120].Trim(' ', '.');
            }

            return text.Length > 0 ? text : "UltimateRDP";
        }
    }

    /// <summary>The name of the .rdp file next to the launcher.</summary>
    public string RdpFileName => FileBaseName + ".rdp";

    /// <summary>The options of the connection, the texts trimmed; null with the field at fault in <paramref name="error"/>.</summary>
    public ConnectionOptions? ToConnection(out LauncherFieldError? error)
    {
        error = null;
        if (Server.Trim().Length == 0)
        {
            error = new LauncherFieldError(LauncherField.Server, null);
            return null;
        }

        int? port = null;
        if (Port.Trim() is { Length: > 0 } portText)
        {
            if (LaunchValues.ParseNumber(portText) is not int number || !LaunchValues.IsPort(number))
            {
                error = new LauncherFieldError(LauncherField.Port, LaunchValueRule.Port);
                return null;
            }

            port = number;
        }

        if (RemoteApp && Program.Trim().Length == 0)
        {
            error = new LauncherFieldError(LauncherField.Program, null);
            return null;
        }

        var connection = new ConnectionOptions
        {
            Server = Server.Trim(),
            Port = port == LaunchValues.DefaultPort ? null : port,
            User = Optional(User),
            Domain = Optional(Domain),
            RemoteApp = RemoteApp ? Program.Trim() : null,
            RemoteAppName = RemoteApp ? Optional(DisplayName) : null,
            RemoteAppArgs = RemoteApp ? Optional(Arguments) : null,
            Gateway = Optional(Gateway),
            Multimon = Multimon,
            Scale = Scale,
            WindowsKeys = WindowsKeys,
            Clipboard = Clipboard,
            Drives = Drives,
            Printers = Printers,
            Audio = Audio,
        };
        if (connection.Check(hasFile: false) is { } refused)
        {
            error = new LauncherFieldError(FieldOf(refused.Option), refused.Rule);
            return null;
        }

        return connection;
    }

    /// <summary>
    /// The launcher, or null with the field at fault in <paramref name="error"/>.
    /// <paramref name="runningExe"/> is the path of the running UltimateRDP.exe, for
    /// <see cref="LauncherExeKind.FullPath"/>; <paramref name="comments"/> are the lines of the
    /// comment at the top.
    /// </summary>
    public LauncherSpec? ToSpec(string runningExe, IReadOnlyList<string> comments, out LauncherFieldError? error)
    {
        ArgumentNullException.ThrowIfNull(runningExe);
        ArgumentNullException.ThrowIfNull(comments);
        if (ToConnection(out error) is not { } connection)
        {
            return null;
        }

        var exe = ExeKind switch
        {
            LauncherExeKind.FullPath => runningExe,
            LauncherExeKind.WithVariables => TypedExe.Trim(),
            _ => null,
        };
        var options = new LaunchOptions(null, Window, AskPassword, null) { Connection = connection };
        var spec = new LauncherSpec(ExeKind, exe, options, WithRdpFile ? RdpFileName : null, comments);
        var problem = LauncherBatch.Check(spec, out _);
        error = problem switch
        {
            LauncherProblem.None => null,
            LauncherProblem.Exe => new LauncherFieldError(LauncherField.Exe, null, problem),
            LauncherProblem.RdpFileName => new LauncherFieldError(LauncherField.Name, null, problem),
            _ => new LauncherFieldError(LauncherField.Arguments, null, problem),
        };
        return error is null ? spec : null;
    }

    /// <summary>
    /// The fields for the connection of <paramref name="file"/>: the server, the account, the
    /// program, the gateway, the password prompt and the display and redirection keys it sets.
    /// The name, the window and where UltimateRDP.exe is stay as they are in <paramref name="current"/>.
    /// </summary>
    public static LauncherDraft FromRdpFile(RdpFile file, LauncherDraft current)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(current);
        var connection = ConnectionOptions.FromRdpFile(file);
        return current with
        {
            Name = string.Empty,
            Server = connection.Server ?? string.Empty,
            Port = (connection.Port ?? LaunchValues.DefaultPort).ToString(CultureInfo.InvariantCulture),
            User = connection.User ?? string.Empty,
            Domain = connection.Domain ?? string.Empty,
            RemoteApp = connection.RemoteApp is not null,
            Program = connection.RemoteApp ?? string.Empty,
            DisplayName = connection.RemoteAppName ?? string.Empty,
            Arguments = connection.RemoteAppArgs ?? string.Empty,
            Gateway = connection.Gateway ?? string.Empty,
            AskPassword = file.PromptForCredentials,
            Multimon = connection.Multimon,
            Scale = connection.Scale,
            WindowsKeys = connection.WindowsKeys,
            Clipboard = connection.Clipboard,
            Drives = connection.Drives,
            Printers = connection.Printers,
            Audio = connection.Audio,
        };
    }

    private static string? Optional(string text) => text.Trim() is { Length: > 0 } value ? value : null;

    private static LauncherField FieldOf(string? option) => option switch
    {
        LaunchOptionNames.Server => LauncherField.Server,
        LaunchOptionNames.Port => LauncherField.Port,
        LaunchOptionNames.User => LauncherField.User,
        LaunchOptionNames.Domain => LauncherField.Domain,
        LaunchOptionNames.RemoteApp => LauncherField.Program,
        LaunchOptionNames.RemoteAppName => LauncherField.DisplayName,
        LaunchOptionNames.Gateway => LauncherField.Gateway,
        _ => LauncherField.Arguments,
    };
}
