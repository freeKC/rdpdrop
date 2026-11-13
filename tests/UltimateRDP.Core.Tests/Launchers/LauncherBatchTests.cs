using System.Text;
using UltimateRDP.Core.Launchers;
using UltimateRDP.Core.RdpFiles;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Core.Tests.Launchers;

/// <summary>The launchers of 0.10.0: a .bat file that cmd.exe runs, read back by the parser of the command line.</summary>
public class LauncherBatchTests
{
    private const string Folder = @"C:\Users\user\Desktop\Launchers\";

    private static readonly Dictionary<string, string> Environment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LOCALAPPDATA"] = @"C:\Users\user\AppData\Local",
        ["ProgramFiles"] = @"C:\Program Files",
        ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
    };

    /// <summary>Values that cmd.exe or the command line could read as something else.</summary>
    public static TheoryData<string> HostileArguments =>
    [
        "a & calc",
        "a && calc",
        "x\" & calc & \"",
        "\" & calc",
        "\"\" & calc & \"\"",
        "a | more",
        "a > C:\\out.txt",
        "a < C:\\in.txt",
        "^",
        "a^",
        "^& calc",
        "%PATH%",
        "%%PATH%%",
        "50%",
        "%~dp0",
        "%1",
        "!PATH!",
        "(x)",
        ") & calc & (",
        @"C:\folder\",
        @"\\fileserver\share\",
        @"C:\a b\c\""",
        "\"",
        "\"\"",
        "\\\"",
        "\\\\\"",
        @"a\\""b",
        "trailing\\",
        "trailing\\\\",
        "quote at end\"",
        "\"start quote",
        "two  spaces",
        "/c calc",
        "--server evil.example.com",
        "é ü ß 日本語",
        "emoji \U0001F600 here",
        "tab\u00A0nbsp",
        "rem & calc",
        "a \"b\" c \"d & e\" f",
        "\\\" & calc & \\\"",
    ];

    [Theory]
    [MemberData(nameof(HostileArguments))]
    public void A_hostile_argument_reaches_the_program_as_it_is_and_adds_no_command(string value)
    {
        var options = Desktop with { RemoteApp = "||Calc", RemoteAppArgs = value };

        var (exe, args) = RunLauncher(Spec(options));

        Assert.Equal(@"C:\Tools\UltimateRDP\UltimateRDP.exe", exe);
        var parsed = LaunchOptions.Parse(args);
        Assert.Null(parsed.Error);
        Assert.Equal(value, parsed.Connection.RemoteAppArgs);
    }

    [Theory]
    [MemberData(nameof(HostileArguments))]
    public void A_hostile_display_name_reaches_the_program_as_it_is_when_it_is_a_name(string value)
    {
        if (!LaunchValues.IsName(value))
        {
            return;
        }

        var options = Desktop with { RemoteApp = "||Calc", RemoteAppName = value };

        var (_, args) = RunLauncher(Spec(options));

        Assert.Equal(value, LaunchOptions.Parse(args).Connection.RemoteAppName);
    }

    [Fact]
    public void Every_option_reads_back_exactly()
    {
        var connection = new ConnectionOptions
        {
            Server = "rds.example.com",
            Port = 3390,
            User = "jdupont",
            Domain = "EXAMPLE",
            RemoteApp = @"C:\Program Files\Example & Co\app (x64).exe",
            RemoteAppName = "Gestion 100% Rémi",
            RemoteAppArgs = "/open \"C:\\Data\\file 1.txt\" /mode=^fast & more",
            Gateway = "gateway.example.com",
            Multimon = true,
            Scale = 150,
            WindowsKeys = WindowsKeysTarget.FullScreen,
            Clipboard = false,
            Drives = true,
            Printers = false,
            Audio = AudioTarget.Remote,
        };
        var options = new LaunchOptions(null, LaunchWindow.Show, true, null) { Connection = connection };

        var (_, args) = RunLauncher(new LauncherSpec(LauncherExeKind.FullPath, @"C:\Tools\UltimateRDP\UltimateRDP.exe", options, null, ["Opens the program."]));

        var parsed = LaunchOptions.Parse(args);
        Assert.Null(parsed.Error);
        Assert.Equal(connection, parsed.Connection);
        Assert.Equal(LaunchWindow.Show, parsed.Window);
        Assert.True(parsed.AskCredentials);
        Assert.Null(parsed.Path);
    }

    [Theory]
    [InlineData(LaunchWindow.Auto, false)]
    [InlineData(LaunchWindow.Hidden, true)]
    [InlineData(LaunchWindow.Show, false)]
    public void The_window_and_the_password_prompt_read_back(LaunchWindow window, bool ask)
    {
        var options = new LaunchOptions(null, window, ask, null) { Connection = Desktop };

        var (_, args) = RunLauncher(new LauncherSpec(LauncherExeKind.FullPath, @"C:\Tools\UltimateRDP.exe", options, null, []));

        var parsed = LaunchOptions.Parse(args);
        Assert.Equal(window, parsed.Window);
        Assert.Equal(ask, parsed.AskCredentials);
        Assert.Equal(Desktop, parsed.Connection);
    }

    [Fact]
    public void Random_values_of_every_text_option_read_back()
    {
        // A fixed seed: the same values on every run.
        var random = new Random(20261113);
        const string alphabet = "aZ09 \"\\^&|<>()%!~:;,=@#'`.-_/*?[]{}$+éß日\t";
        var tried = 0;
        for (var round = 0; round < 3000; round++)
        {
            var value = new string(Enumerable.Range(0, random.Next(1, 24)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            if (!LaunchValues.IsArguments(value))
            {
                continue;
            }

            tried++;
            var connection = Desktop with
            {
                RemoteApp = LaunchValues.IsProgram(value) ? value : "||Calc",
                RemoteAppArgs = value,
                RemoteAppName = LaunchValues.IsName(value) ? value : null,
                User = LaunchValues.IsName(value) ? value : null,
            };

            var (_, args) = RunLauncher(Spec(connection));

            var parsed = LaunchOptions.Parse(args);
            Assert.True(parsed.Error is null, $"{value}: {parsed.Error}");
            Assert.Equal(connection, parsed.Connection);
        }

        Assert.True(tried > 1000);
    }

    [Theory]
    [InlineData(LauncherExeKind.FullPath, @"C:\Apps\50% off & more (x86)\UltimateRDP.exe", @"C:\Apps\50% off & more (x86)\UltimateRDP.exe")]
    [InlineData(LauncherExeKind.FullPath, @"C:\Apps\%LOCALAPPDATA%\UltimateRDP.exe", @"C:\Apps\%LOCALAPPDATA%\UltimateRDP.exe")]
    [InlineData(LauncherExeKind.NextToLauncher, null, Folder + "UltimateRDP.exe")]
    [InlineData(LauncherExeKind.WithVariables, @"%LOCALAPPDATA%\UltimateRDP\client-0.10.0\UltimateRDP.exe", @"C:\Users\user\AppData\Local\UltimateRDP\client-0.10.0\UltimateRDP.exe")]
    [InlineData(LauncherExeKind.WithVariables, @"%ProgramFiles(x86)%\UltimateRDP\UltimateRDP.exe", @"C:\Program Files (x86)\UltimateRDP\UltimateRDP.exe")]
    [InlineData(LauncherExeKind.WithVariables, @"%~dp0bin\UltimateRDP.exe", Folder + @"bin\UltimateRDP.exe")]
    public void The_program_is_found_where_the_launcher_says(LauncherExeKind kind, string? exe, string expected)
    {
        var options = new LaunchOptions(null, LaunchWindow.Auto, false, null) { Connection = Desktop };

        var (found, _) = RunLauncher(new LauncherSpec(kind, exe, options, null, []));

        Assert.Equal(expected, found);
    }

    [Theory]
    [InlineData(LauncherExeKind.FullPath, "")]
    [InlineData(LauncherExeKind.FullPath, null)]
    [InlineData(LauncherExeKind.FullPath, "C:\\a\"b\\UltimateRDP.exe")]
    [InlineData(LauncherExeKind.FullPath, "C:\\a\r\nb\\UltimateRDP.exe")]
    [InlineData(LauncherExeKind.FullPath, "C:\\Apps\\")]
    [InlineData(LauncherExeKind.WithVariables, @"%LOCALAPPDATA\UltimateRDP.exe")]
    [InlineData(LauncherExeKind.WithVariables, @"C:\50%\UltimateRDP.exe")]
    [InlineData(LauncherExeKind.WithVariables, @"%A B%\UltimateRDP.exe")]
    [InlineData(LauncherExeKind.WithVariables, @"%~f0\UltimateRDP.exe")]
    [InlineData(LauncherExeKind.WithVariables, @"%1\UltimateRDP.exe")]
    [InlineData(LauncherExeKind.WithVariables, @"%%\UltimateRDP.exe")]
    public void A_wrong_path_of_the_program_is_refused(LauncherExeKind kind, string? exe)
    {
        var spec = new LauncherSpec(kind, exe, new LaunchOptions(null, LaunchWindow.Auto, false, null) { Connection = Desktop }, null, []);

        Assert.Equal(LauncherProblem.Exe, LauncherBatch.Check(spec, out _));
        Assert.Throws<ArgumentException>(() => LauncherBatch.Build(spec));
    }

    [Fact]
    public void A_launcher_with_a_file_names_the_file_next_to_it_and_the_file_holds_the_connection()
    {
        var connection = Desktop with { RemoteApp = "||Notepad", RemoteAppName = "Bloc-notes", User = "jdupont" };
        var options = new LaunchOptions(null, LaunchWindow.Auto, true, null) { Connection = connection };
        var spec = new LauncherSpec(LauncherExeKind.NextToLauncher, null, options, "Bloc-notes & co (100%).rdp".Replace("%", string.Empty, StringComparison.Ordinal), []);

        var (_, args) = RunLauncher(spec);
        var parsed = LaunchOptions.Parse(args);

        Assert.Null(parsed.Error);
        Assert.Equal(Folder + "Bloc-notes & co (100).rdp", parsed.Path);
        Assert.True(parsed.AskCredentials);
        Assert.True(parsed.Connection.IsEmpty);
        var file = RdpFile.Parse(LauncherBatch.RdpContent(connection));
        Assert.Equal(connection with { Port = LaunchValues.DefaultPort }, ConnectionOptions.FromRdpFile(file));
    }

    [Theory]
    [InlineData("launcher.txt")]
    [InlineData(".rdp")]
    [InlineData("a\\b.rdp")]
    [InlineData("a/b.rdp")]
    [InlineData("a\"b.rdp")]
    [InlineData("50%.rdp")]
    [InlineData("a:b.rdp")]
    [InlineData("a\nb.rdp")]
    [InlineData(" a.rdp")]
    public void A_wrong_name_of_the_rdp_file_is_refused(string name)
    {
        var spec = new LauncherSpec(LauncherExeKind.NextToLauncher, null, new LaunchOptions(null, LaunchWindow.Auto, false, null) { Connection = Desktop }, name, []);

        Assert.Equal(LauncherProblem.RdpFileName, LauncherBatch.Check(spec, out _));
    }

    [Fact]
    public void A_refused_connection_is_not_written()
    {
        var spec = Spec(Desktop with { Server = "bad host" });

        Assert.Equal(LauncherProblem.Connection, LauncherBatch.Check(spec, out var error));
        Assert.Equal(LaunchErrorKind.InvalidValue, error?.Kind);
        Assert.Throws<ArgumentException>(() => LauncherBatch.Build(spec));
    }

    [Fact]
    public void A_command_line_longer_than_cmd_reads_is_refused()
    {
        var spec = Spec(Desktop with { RemoteApp = "||Calc", RemoteAppArgs = new string('&', LaunchValues.MaxArgumentsLength) });

        Assert.Equal(LauncherProblem.None, LauncherBatch.Check(spec, out _));
        Assert.True(LauncherBatch.Build(spec).Length < LauncherBatch.MaxLineLength);
    }

    [Fact]
    public void The_file_has_its_header_crlf_and_chcp_only_when_a_text_needs_it()
    {
        var ascii = LauncherBatch.Build(new LauncherSpec(LauncherExeKind.FullPath, @"C:\Tools\UltimateRDP.exe", new LaunchOptions(null, LaunchWindow.Auto, false, null) { Connection = Desktop }, null, ["Opens the desktop of server.example.com.", "It holds no password."]));
        var accents = LauncherBatch.Build(new LauncherSpec(LauncherExeKind.FullPath, @"C:\Tools\UltimateRDP.exe", new LaunchOptions(null, LaunchWindow.Auto, false, null) { Connection = Desktop with { User = "rémi" } }, null, []));

        Assert.Equal(
            "@echo off\r\nrem Opens the desktop of server.example.com.\r\nrem It holds no password.\r\nsetlocal DisableDelayedExpansion\r\nstart \"\" \"C:\\Tools\\UltimateRDP.exe\" --server \"server.example.com\"\r\n",
            ascii);
        Assert.StartsWith("@echo off\r\nchcp 65001 >nul\r\n", accents, StringComparison.Ordinal);
        Assert.DoesNotContain("chcp", ascii, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("100% sure", "100%% sure")]
    [InlineData("ends with a caret^", "ends with a caret")]
    [InlineData("two\r\nlines", "two  lines")]
    [InlineData("R&D | <tests> \"quoted\"", "R&D | <tests> \"quoted\"")]
    public void A_comment_cannot_join_the_next_line_or_expand_a_variable(string comment, string written)
    {
        var text = LauncherBatch.Build(new LauncherSpec(LauncherExeKind.FullPath, @"C:\Tools\UltimateRDP.exe", new LaunchOptions(null, LaunchWindow.Auto, false, null) { Connection = Desktop }, null, [comment]));

        Assert.Contains("\r\nrem " + written + "\r\n", text, StringComparison.Ordinal);
        RunText(text);
    }

    [Fact]
    public void The_bytes_are_utf8_without_a_byte_order_mark()
    {
        var bytes = LauncherBatch.FileEncoding.GetBytes("é");

        Assert.Equal(new byte[] { 0xC3, 0xA9 }, bytes);
        Assert.Empty(LauncherBatch.FileEncoding.GetPreamble());
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData(@"C:\dir\", @"""C:\dir\\""")]
    [InlineData(@"a\""b", @"""a\\\""b""")]
    [InlineData(@"a\\b", @"""a\\b""")]
    public void A_value_is_quoted_with_the_rules_of_the_windows_command_line(string value, string quoted)
    {
        Assert.Equal(quoted, LauncherBatch.QuoteForCommandLine(value));
        Assert.Equal([value], CmdModel.SplitArguments(" " + quoted));
    }

    [Fact]
    public void A_caret_goes_before_a_special_character_where_cmd_is_outside_quotes()
    {
        var inside = false;

        var written = LauncherBatch.QuoteArgument("x\" & calc", ref inside);

        Assert.Equal("\"x\\\" ^& calc\"", written);

        // cmd.exe sees three quotes: it is inside quotes after them, and the next argument starts there.
        Assert.True(inside);
    }

    private static ConnectionOptions Desktop { get; } = new() { Server = "server.example.com" };

    private static LauncherSpec Spec(ConnectionOptions connection) =>
        new(LauncherExeKind.FullPath, @"C:\Tools\UltimateRDP\UltimateRDP.exe", new LaunchOptions(null, LaunchWindow.Auto, false, null) { Connection = connection }, null, ["Opens a program."]);

    private static (string Exe, IReadOnlyList<string> Args) RunLauncher(LauncherSpec spec) => RunText(LauncherBatch.Build(spec));

    private static (string Exe, IReadOnlyList<string> Args) RunText(string text)
    {
        // What the file holds once saved and read again.
        var read = Encoding.UTF8.GetString(LauncherBatch.FileEncoding.GetBytes(text));
        return CmdModel.Run(read, Folder, Environment);
    }
}
