namespace UltimateRDP.Core.Launchers;

/// <summary>Why a command line of the client is refused.</summary>
public enum LaunchErrorKind
{
    /// <summary>An option the client does not know.</summary>
    UnknownOption,

    /// <summary>An option that needs a value is the last argument, or is followed by another option.</summary>
    MissingValue,

    /// <summary>A value does not follow its <see cref="LaunchValueRule"/>.</summary>
    InvalidValue,

    /// <summary>An option with a value is given twice.</summary>
    Repeated,

    /// <summary><c>--show</c> and <c>--hidden</c> together.</summary>
    ShowAndHidden,

    /// <summary>More than one .rdp file.</summary>
    TwoFiles,

    /// <summary>Options of a connection without <c>--server</c> and without a .rdp file.</summary>
    NoServer,

    /// <summary><c>--remoteapp-name</c> or <c>--remoteapp-args</c> without <c>--remoteapp</c> and without a .rdp file.</summary>
    NoProgram,

    /// <summary>An option for a password: there is none, on purpose.</summary>
    Password,
}

/// <summary>A refused command line: what is wrong, with the option and the value at fault.</summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="Option">The option at fault, or null.</param>
/// <param name="Value">The value at fault, or null.</param>
/// <param name="Rule">What the value must be, for <see cref="LaunchErrorKind.InvalidValue"/>.</param>
public sealed record LaunchError(LaunchErrorKind Kind, string? Option = null, string? Value = null, LaunchValueRule? Rule = null)
{
    /// <summary>The error in English, for the log and the tests; the client shows its own texts.</summary>
    public string Message => Kind switch
    {
        LaunchErrorKind.UnknownOption => $"Unknown option {Option}.",
        LaunchErrorKind.MissingValue => $"The option {Option} needs a value.",
        LaunchErrorKind.InvalidValue => $"The value of {Option} is not valid ({Rule}).",
        LaunchErrorKind.Repeated => $"The option {Option} is given more than once.",
        LaunchErrorKind.ShowAndHidden => $"{LaunchOptionNames.Show} and {LaunchOptionNames.Hidden} cannot be used together.",
        LaunchErrorKind.TwoFiles => "Only one .rdp file can be given.",
        LaunchErrorKind.NoServer => $"The options of a connection need {LaunchOptionNames.Server} or a .rdp file.",
        LaunchErrorKind.NoProgram => $"{Option} needs {LaunchOptionNames.RemoteApp} or a .rdp file.",
        LaunchErrorKind.Password => "A password is never accepted on the command line.",
        _ => Kind.ToString(),
    };

    /// <inheritdoc/>
    public override string ToString() => Message;
}
