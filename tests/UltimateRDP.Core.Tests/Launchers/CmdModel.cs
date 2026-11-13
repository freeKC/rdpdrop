using System.Text;

namespace UltimateRDP.Core.Tests.Launchers;

/// <summary>Thrown when a line of a batch file would do more than the launcher means.</summary>
public sealed class CmdInjectionException(string message) : Exception(message);

/// <summary>
/// A model of how cmd.exe reads the lines of a launcher, and of how the Windows command line
/// is split into arguments, for the round trip tests. It follows the documented phases of
/// cmd.exe: first the percent expansion (<c>%%</c>, <c>%~dp0</c>, <c>%NAME%</c>), then the
/// quotes and the carets, where an unescaped <c>&amp; | &lt; &gt;</c> outside quotes would
/// start another command or a redirection.
/// </summary>
internal static class CmdModel
{
    /// <summary>
    /// Reads the batch file and returns the program and the arguments of its <c>start</c>
    /// line as the program receives them. <paramref name="folder"/> is what <c>%~dp0</c>
    /// expands to, with its final backslash.
    /// </summary>
    public static (string Exe, IReadOnlyList<string> Args) Run(string batch, string folder, IReadOnlyDictionary<string, string> environment)
    {
        Assert.DoesNotContain("\n", batch.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("\r", batch.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.EndsWith("\r\n", batch, StringComparison.Ordinal);
        var lines = batch[..^2].Split("\r\n");
        Assert.Equal("@echo off", lines[0]);
        string? start = null;
        for (var i = 1; i < lines.Length; i++)
        {
            var expanded = ExpandPercents(lines[i], folder, environment);
            if (expanded.StartsWith("rem ", StringComparison.Ordinal) || expanded == "rem")
            {
                // A remark ends at its line unless a caret ends it.
                if (expanded.EndsWith('^'))
                {
                    throw new CmdInjectionException($"The remark on line {i + 1} goes on to the next line.");
                }

                continue;
            }

            if (expanded is "chcp 65001 >nul" or "setlocal DisableDelayedExpansion")
            {
                Assert.Null(start);
                continue;
            }

            Assert.True(start is null && i == lines.Length - 1, $"Unexpected line {i + 1}: {lines[i]}");
            start = RemoveCarets(expanded);
        }

        Assert.NotNull(start);
        const string prefix = "start \"\" \"";
        Assert.StartsWith(prefix, start, StringComparison.Ordinal);
        var close = start.IndexOf('"', prefix.Length);
        Assert.True(close > 0);
        var exe = start[prefix.Length..close];
        var rest = start[(close + 1)..];
        Assert.True(rest.Length == 0 || rest[0] == ' ', "The program and its arguments are not apart.");
        return (exe, SplitArguments(rest));
    }

    /// <summary>The first phase of cmd.exe in a batch file.</summary>
    public static string ExpandPercents(string line, string folder, IReadOnlyDictionary<string, string> environment)
    {
        var text = new StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c != '%')
            {
                text.Append(c);
                continue;
            }

            if (i + 1 < line.Length && line[i + 1] == '%')
            {
                text.Append('%');
                i++;
                continue;
            }

            if (i + 1 < line.Length && line[i + 1] == '~')
            {
                if (string.CompareOrdinal(line, i, "%~dp0", 0, 5) != 0)
                {
                    throw new CmdInjectionException($"An argument reference other than %~dp0: {line[i..]}");
                }

                text.Append(folder);
                i += 4;
                continue;
            }

            if (i + 1 < line.Length && char.IsAsciiDigit(line[i + 1]))
            {
                throw new CmdInjectionException($"A batch argument reference: {line[i..]}");
            }

            var end = line.IndexOf('%', i + 1);
            if (end < 0)
            {
                // A lone percent is removed.
                continue;
            }

            var name = line[(i + 1)..end];
            if (!environment.TryGetValue(name, out var value))
            {
                throw new CmdInjectionException($"A variable %{name}% would expand.");
            }

            text.Append(value);
            i = end;
        }

        return text.ToString();
    }

    /// <summary>The second phase: quotes toggle, a caret outside quotes takes the next character as it is.</summary>
    public static string RemoveCarets(string line)
    {
        var text = new StringBuilder();
        var inside = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inside = !inside;
                text.Append(c);
            }
            else if (inside)
            {
                text.Append(c);
            }
            else if (c == '^')
            {
                if (i + 1 >= line.Length)
                {
                    throw new CmdInjectionException("A caret at the end of the line joins the next one.");
                }

                text.Append(line[++i]);
            }
            else if (c is '&' or '|' or '<' or '>')
            {
                throw new CmdInjectionException($"'{c}' outside quotes at {i}: {line}");
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    /// <summary>The rules of CommandLineToArgvW and of the .NET runtime for the arguments after the program.</summary>
    public static IReadOnlyList<string> SplitArguments(string text)
    {
        var args = new List<string>();
        var i = 0;
        while (true)
        {
            while (i < text.Length && text[i] is ' ' or '\t')
            {
                i++;
            }

            if (i >= text.Length)
            {
                return args;
            }

            var arg = new StringBuilder();
            var inside = false;
            while (i < text.Length && (inside || text[i] is not (' ' or '\t')))
            {
                var c = text[i];
                if (c == '\\')
                {
                    var count = 0;
                    while (i < text.Length && text[i] == '\\')
                    {
                        count++;
                        i++;
                    }

                    if (i < text.Length && text[i] == '"')
                    {
                        arg.Append('\\', count / 2);
                        if (count % 2 == 1)
                        {
                            arg.Append('"');
                            i++;
                        }
                    }
                    else
                    {
                        arg.Append('\\', count);
                    }
                }
                else if (c == '"')
                {
                    if (inside && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        arg.Append('"');
                        i += 2;
                    }
                    else
                    {
                        inside = !inside;
                        i++;
                    }
                }
                else
                {
                    arg.Append(c);
                    i++;
                }
            }

            args.Add(arg.ToString());
        }
    }
}
