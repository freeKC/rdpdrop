using System.Text;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Core.Launchers;

/// <summary>Where the launcher finds UltimateRDP.exe.</summary>
public enum LauncherExeKind
{
    /// <summary>A full path, written as it is: a <c>%</c> in it is a <c>%</c>.</summary>
    FullPath,

    /// <summary><c>%~dp0UltimateRDP.exe</c>: next to the launcher.</summary>
    NextToLauncher,

    /// <summary>
    /// A path typed by the administrator, where environment variables such as
    /// <c>%LOCALAPPDATA%</c> or <c>%ProgramFiles%</c> stay as written and expand when the
    /// launcher runs.
    /// </summary>
    WithVariables,
}

/// <summary>What a launcher does.</summary>
/// <param name="ExeKind">How <paramref name="Exe"/> is written.</param>
/// <param name="Exe">The path of UltimateRDP.exe; ignored for <see cref="LauncherExeKind.NextToLauncher"/>.</param>
/// <param name="Options">
/// The command line: the window, the password prompt, and either the options of the
/// connection or nothing when <paramref name="RdpFileName"/> is given.
/// </param>
/// <param name="RdpFileName">
/// The name of a .rdp file next to the launcher that holds the connection, or null for a
/// launcher that carries every option on its command line.
/// </param>
/// <param name="Comments">The lines of the comment at the top, in the language of the user.</param>
public sealed record LauncherSpec(
    LauncherExeKind ExeKind,
    string? Exe,
    LaunchOptions Options,
    string? RdpFileName,
    IReadOnlyList<string> Comments);

/// <summary>Why a launcher cannot be written.</summary>
public enum LauncherProblem
{
    /// <summary>Nothing: it can be written.</summary>
    None,

    /// <summary>The path of UltimateRDP.exe is empty, has a quote or a control character, ends with <c>\</c>, or has a <c>%</c> that does not close a variable.</summary>
    Exe,

    /// <summary>The name of the .rdp file is not a plain file name ending with <c>.rdp</c>.</summary>
    RdpFileName,

    /// <summary>The connection is refused, see <see cref="LaunchError"/>.</summary>
    Connection,

    /// <summary>The command line would be longer than cmd.exe reads.</summary>
    TooLong,
}

/// <summary>
/// Writes a launcher: a .bat file that starts UltimateRDP.exe with the options of one
/// connection, so that an end user only double clicks it. It holds no password.
/// </summary>
/// <remarks>
/// <para>
/// Every value is written so that cmd.exe gives it to the client exactly, and so that no value
/// can add a command. Each value is put in double quotes with the rules of the Windows command
/// line (a quote inside becomes <c>\"</c>, backslashes before a quote and at the end are
/// doubled). cmd.exe does not know these rules: it sees a quote as the start or the end of a
/// quoted part. The writer follows what cmd.exe sees, and puts a <c>^</c> before
/// <c>&amp; | &lt; &gt; ^ ( )</c> wherever cmd.exe is outside quotes. Every <c>%</c> of a
/// value is doubled, so that no variable expands; only the path of the program, in the
/// <see cref="LauncherExeKind.NextToLauncher"/> and <see cref="LauncherExeKind.WithVariables"/>
/// forms, keeps its variables. Delayed expansion is turned off, so a <c>!</c> stays a
/// <c>!</c>. Line breaks are refused in every value by <see cref="LaunchValues"/>.
/// </para>
/// <para>
/// The text is written with CRLF line ends and is meant to be saved in UTF-8 without a byte
/// order mark (<see cref="FileEncoding"/>); when the command line has a character outside
/// ASCII, <c>chcp 65001</c> comes right after <c>@echo off</c> so that cmd.exe reads the
/// following lines as UTF-8.
/// </para>
/// </remarks>
public static class LauncherBatch
{
    /// <summary>The name of the program.</summary>
    public const string ExeName = "UltimateRDP.exe";

    /// <summary>What <see cref="LauncherExeKind.NextToLauncher"/> writes.</summary>
    public const string NextToLauncherExe = "%~dp0" + ExeName;

    /// <summary>
    /// Longest command line written, in characters: cmd.exe reads 8191, and the folder that
    /// <c>%~dp0</c> or a variable expands to needs room.
    /// </summary>
    public const int MaxLineLength = 7600;

    /// <summary>Longest name of a .rdp file, in characters.</summary>
    public const int MaxRdpFileNameLength = 200;

    /// <summary>UTF-8 without a byte order mark.</summary>
    public static Encoding FileEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private const string NewLine = "\r\n";

    private static readonly char[] CmdSpecials = ['&', '|', '<', '>', '^', '(', ')'];

    private static readonly char[] FileNameRefused = ['"', '\\', '/', ':', '*', '?', '<', '>', '|', '%'];

    /// <summary>What prevents <paramref name="spec"/> from being written, or <see cref="LauncherProblem.None"/>.</summary>
    public static LauncherProblem Check(LauncherSpec spec, out LaunchError? connectionError)
    {
        ArgumentNullException.ThrowIfNull(spec);
        connectionError = null;
        if (spec.ExeKind != LauncherExeKind.NextToLauncher && !IsExe(spec.Exe, spec.ExeKind == LauncherExeKind.WithVariables))
        {
            return LauncherProblem.Exe;
        }

        if (spec.RdpFileName is { } name && !IsRdpFileName(name))
        {
            return LauncherProblem.RdpFileName;
        }

        var options = spec.Options;
        connectionError = options.Error ?? options.Connection.Check(hasFile: spec.RdpFileName is not null || options.Path is not null);
        if (connectionError is not null)
        {
            return LauncherProblem.Connection;
        }

        return StartLine(spec).Length > MaxLineLength ? LauncherProblem.TooLong : LauncherProblem.None;
    }

    /// <summary>
    /// The text of the launcher. With <see cref="LauncherSpec.RdpFileName"/> the connection is in
    /// that file (<see cref="RdpContent"/>) and the command line only names it.
    /// </summary>
    /// <exception cref="ArgumentException">The launcher cannot be written; see <see cref="Check"/>.</exception>
    public static string Build(LauncherSpec spec)
    {
        if (Check(spec, out var error) is var problem and not LauncherProblem.None)
        {
            throw new ArgumentException($"The launcher cannot be written: {problem} {error?.Message}", nameof(spec));
        }

        var start = StartLine(spec);
        var comments = spec.Comments.Select(CommentText).ToList();
        var text = new StringBuilder();
        text.Append("@echo off").Append(NewLine);

        // Before any other line, so that cmd.exe reads every following line as UTF-8.
        if (start.Any(c => c > 127) || comments.Any(line => line.Any(c => c > 127)))
        {
            text.Append("chcp 65001 >nul").Append(NewLine);
        }

        foreach (var comment in comments)
        {
            text.Append("rem ").Append(comment).Append(NewLine);
        }

        text.Append("setlocal DisableDelayedExpansion").Append(NewLine);
        text.Append(start).Append(NewLine);
        return text.ToString();
    }

    /// <summary>
    /// The text of the .rdp file of a launcher with <see cref="LauncherSpec.RdpFileName"/>:
    /// the lines of <paramref name="connection"/>, CRLF.
    /// </summary>
    public static string RdpContent(ConnectionOptions connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.ToRdpContent(null);
    }

    /// <summary>
    /// The arguments the client receives from the launcher, in order, the path of the .rdp
    /// file given as <c>%~dp0</c> followed by its name.
    /// </summary>
    public static IReadOnlyList<string> Arguments(LauncherSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var options = spec.RdpFileName is null ? spec.Options : spec.Options with { Connection = ConnectionOptions.None, Path = null };
        var args = options.ToArguments().ToList();
        if (spec.RdpFileName is { } name)
        {
            args.Add("%~dp0" + name);
        }

        return args;
    }

    /// <summary>
    /// <paramref name="value"/> as one argument of a command line run by cmd.exe from a
    /// batch file, starting outside quotes; <paramref name="insideQuotes"/> follows what
    /// cmd.exe sees, from one argument to the next.
    /// </summary>
    public static string QuoteArgument(string value, ref bool insideQuotes)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = new StringBuilder();
        foreach (var c in QuoteForCommandLine(value))
        {
            if (c == '"')
            {
                insideQuotes = !insideQuotes;
                text.Append(c);
            }
            else if (c == '%')
            {
                text.Append("%%");
            }
            else if (!insideQuotes && Array.IndexOf(CmdSpecials, c) >= 0)
            {
                text.Append('^').Append(c);
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// <paramref name="value"/> in double quotes with the rules of the Windows command line
    /// (CommandLineToArgvW and the .NET runtime): <c>\"</c> for a quote, backslashes before a
    /// quote or the closing quote doubled.
    /// </summary>
    public static string QuoteForCommandLine(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                text.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                text.Append('\\', backslashes);
            }

            text.Append(c);
            backslashes = 0;
        }

        text.Append('\\', backslashes * 2).Append('"');
        return text.ToString();
    }

    /// <summary>
    /// True for a path of UltimateRDP.exe: not empty, at most 1024 characters, without quotes,
    /// control characters or a <c>\</c> at the end. With <paramref name="variables"/>, every
    /// <c>%</c> opens and closes a variable name of letters, digits, <c>_</c>, <c>(</c> and
    /// <c>)</c>, or starts <c>%~dp0</c>.
    /// </summary>
    public static bool IsExe(string? path, bool variables)
    {
        if (!LaunchValues.IsText(path, LaunchValues.MaxProgramLength, allowQuotes: false) || path!.EndsWith('\\'))
        {
            return false;
        }

        if (!variables)
        {
            return true;
        }

        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] != '%')
            {
                continue;
            }

            if (string.CompareOrdinal(path, i, "%~dp0", 0, 5) == 0)
            {
                i += 4;
                continue;
            }

            var close = path.IndexOf('%', i + 1);
            if (close <= i + 1 || !path[(i + 1)..close].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '(' or ')'))
            {
                return false;
            }

            i = close;
        }

        return true;
    }

    /// <summary>True for a plain file name ending with <c>.rdp</c>, without a folder, quotes, <c>%</c> or control characters.</summary>
    public static bool IsRdpFileName(string? name) =>
        LaunchValues.IsText(name, MaxRdpFileNameLength, allowQuotes: false)
        && name!.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase)
        && name.Length > 4
        && name.IndexOfAny(FileNameRefused) < 0
        && !name.EndsWith('.')
        && !name.StartsWith('.');

    /// <summary>
    /// A line of the comment as cmd.exe ignores it: control characters become spaces, a
    /// <c>%</c> is doubled (cmd.exe expands variables before it sees <c>rem</c>) and a
    /// <c>^</c> is left out (one at the end would join the next line to the comment).
    /// </summary>
    public static string CommentText(string comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        var text = new StringBuilder();
        foreach (var c in comment)
        {
            if (c == '%')
            {
                text.Append("%%");
            }
            else if (c != '^')
            {
                text.Append(char.IsControl(c) || c is (char)0x2028 or (char)0x2029 ? ' ' : c);
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string StartLine(LauncherSpec spec)
    {
        var line = new StringBuilder("start \"\" \"");
        line.Append(spec.ExeKind switch
        {
            LauncherExeKind.NextToLauncher => NextToLauncherExe,
            LauncherExeKind.WithVariables => spec.Exe,
            _ => spec.Exe?.Replace("%", "%%", StringComparison.Ordinal),
        });
        line.Append('"');

        var insideQuotes = false;
        var args = Arguments(spec);
        for (var i = 0; i < args.Count; i++)
        {
            line.Append(' ');
            var arg = args[i];
            if (spec.RdpFileName is not null && i == args.Count - 1)
            {
                // %~dp0 expands to the folder of the launcher, with its final backslash.
                line.Append("\"%~dp0").Append(QuoteArgument(spec.RdpFileName, ref insideQuotes)[1..]);
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal) && arg.All(c => char.IsAsciiLetterLower(c) || c == '-') && IsOption(args, i))
            {
                line.Append(arg);
            }
            else
            {
                line.Append(QuoteArgument(arg, ref insideQuotes));
            }
        }

        return line.ToString();
    }

    /// <summary>True when <paramref name="args"/>[<paramref name="index"/>] is an option, not the value of the one before it.</summary>
    private static bool IsOption(IReadOnlyList<string> args, int index)
    {
        var position = 0;
        while (position < index)
        {
            position += args[position] is LaunchOptionNames.Show or LaunchOptionNames.Hidden or LaunchOptionNames.AskCredentials ? 1 : 2;
        }

        return position == index;
    }
}
