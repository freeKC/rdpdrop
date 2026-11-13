using UltimateRDP.Core.Launchers;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Core.Tests.Launchers;

/// <summary>The options of a connection on the command line of the client, since 0.10.0.</summary>
public class LaunchOptionsConnectionTests
{
    [Fact]
    public void Every_option_of_a_connection_is_read()
    {
        var options = LaunchOptions.Parse(
        [
            "--SERVER", "rds.example.com", "--port", "3390", "--user", "jdupont", "--domain", "EXAMPLE",
            "--remoteapp", "Notepad", "--remoteapp-name", "Bloc-notes", "--remoteapp-args", "--readonly C:\\file.txt",
            "--gateway", "gateway.example.com", "--multimon", "ON", "--scale", "125", "--windows-keys", "fullscreen",
            "--clipboard", "off", "--drives", "on", "--printers", "off", "--audio", "remote", "--hidden", "--ask-credentials",
        ]);

        Assert.Null(options.Error);
        Assert.Equal(
            new ConnectionOptions
            {
                Server = "rds.example.com",
                Port = 3390,
                User = "jdupont",
                Domain = "EXAMPLE",
                RemoteApp = "Notepad",
                RemoteAppName = "Bloc-notes",
                RemoteAppArgs = "--readonly C:\\file.txt",
                Gateway = "gateway.example.com",
                Multimon = true,
                Scale = 125,
                WindowsKeys = WindowsKeysTarget.FullScreen,
                Clipboard = false,
                Drives = true,
                Printers = false,
                Audio = AudioTarget.Remote,
            },
            options.Connection);
        Assert.Equal(LaunchWindow.Hidden, options.Window);
        Assert.True(options.AskCredentials);
        Assert.True(options.HasConnection);
        Assert.Null(options.Path);
    }

    [Fact]
    public void A_file_and_options_together_are_read()
    {
        var options = LaunchOptions.Parse(["--user", "jdupont", @"C:\rdp\desktop.rdp", "--port", "3391"]);

        Assert.Null(options.Error);
        Assert.Equal(@"C:\rdp\desktop.rdp", options.Path);
        Assert.Equal(new ConnectionOptions { User = "jdupont", Port = 3391 }, options.Connection);
    }

    [Fact]
    public void Without_any_option_the_connection_is_empty()
    {
        var options = LaunchOptions.Parse([@"C:\rdp\desktop.rdp"]);

        Assert.True(options.Connection.IsEmpty);
        Assert.False(LaunchOptions.Parse([]).HasConnection);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--HELP")]
    [InlineData("/?")]
    public void Help_asks_for_the_list_of_the_options(string arg)
    {
        var options = LaunchOptions.Parse(["--server", "server.example.com", arg]);

        Assert.True(options.Help);
        Assert.Null(options.Error);
        Assert.False(options.HasConnection);
    }

    [Theory]
    [InlineData(LaunchErrorKind.UnknownOption, "--fullscreen")]
    [InlineData(LaunchErrorKind.UnknownOption, "--server=server.example.com")]
    [InlineData(LaunchErrorKind.MissingValue, "--server")]
    [InlineData(LaunchErrorKind.MissingValue, "--user", "--show", "--server", "server.example.com")]
    [InlineData(LaunchErrorKind.MissingValue, "--server", "server.example.com", "--remoteapp-args")]
    [InlineData(LaunchErrorKind.Repeated, "--server", "a.example.com", "--server", "b.example.com")]
    [InlineData(LaunchErrorKind.NoServer, "--user", "jdupont")]
    [InlineData(LaunchErrorKind.NoServer, "--clipboard", "off", "--show")]
    [InlineData(LaunchErrorKind.NoProgram, "--server", "server.example.com", "--remoteapp-name", "Notepad")]
    [InlineData(LaunchErrorKind.NoProgram, "--server", "server.example.com", "--remoteapp-args", "/x")]
    [InlineData(LaunchErrorKind.Password, "--server", "server.example.com", "--password", "secret")]
    [InlineData(LaunchErrorKind.Password, "--PWD=secret")]
    [InlineData(LaunchErrorKind.Password, "--pass", "secret")]
    [InlineData(LaunchErrorKind.ShowAndHidden, "--show", "--hidden")]
    [InlineData(LaunchErrorKind.TwoFiles, "a.rdp", "b.rdp")]
    public void A_wrong_command_line_is_refused_with_its_reason(LaunchErrorKind kind, params string[] args)
    {
        var options = LaunchOptions.Parse(args);

        Assert.Equal(kind, options.Error?.Kind);
        Assert.False(options.HasConnection);
        Assert.True(options.Connection.IsEmpty);
    }

    [Theory]
    [InlineData("--server", "bad host", LaunchValueRule.Host)]
    [InlineData("--server", "server.example.com:3390", LaunchValueRule.Host)]
    [InlineData("--server", "-server.example.com", LaunchValueRule.Host)]
    [InlineData("--server", "server..example.com", LaunchValueRule.Host)]
    [InlineData("--server", "300.1.2.3", LaunchValueRule.Host)]
    [InlineData("--server", "1.2.3", LaunchValueRule.Host)]
    [InlineData("--server", "[server.example.com]", LaunchValueRule.Host)]
    [InlineData("--server", "fe80::1%12", LaunchValueRule.Host)]
    [InlineData("--server", "a&b", LaunchValueRule.Host)]
    [InlineData("--port", "0", LaunchValueRule.Port)]
    [InlineData("--port", "65536", LaunchValueRule.Port)]
    [InlineData("--port", "-1", LaunchValueRule.Port)]
    [InlineData("--port", "+3389", LaunchValueRule.Port)]
    [InlineData("--port", "33 89", LaunchValueRule.Port)]
    [InlineData("--port", "3389x", LaunchValueRule.Port)]
    [InlineData("--user", "a\"b", LaunchValueRule.Name)]
    [InlineData("--user", "a\tb", LaunchValueRule.Name)]
    [InlineData("--domain", "a\u0000b", LaunchValueRule.Name)]
    [InlineData("--remoteapp", "||", LaunchValueRule.Program)]
    [InlineData("--remoteapp", "|||Notepad", LaunchValueRule.Program)]
    [InlineData("--remoteapp", "||C:\\Windows\\notepad.exe", LaunchValueRule.Program)]
    [InlineData("--remoteapp", "a|b", LaunchValueRule.Program)]
    [InlineData("--remoteapp-args", "a\rb", LaunchValueRule.Arguments)]
    [InlineData("--gateway", "gate way", LaunchValueRule.Host)]
    [InlineData("--multimon", "yes", LaunchValueRule.OnOff)]
    [InlineData("--clipboard", "1", LaunchValueRule.OnOff)]
    [InlineData("--scale", "99", LaunchValueRule.Scale)]
    [InlineData("--scale", "501", LaunchValueRule.Scale)]
    [InlineData("--windows-keys", "1", LaunchValueRule.WindowsKeys)]
    [InlineData("--audio", "speakers", LaunchValueRule.Audio)]
    public void A_wrong_value_is_refused_with_its_rule(string option, string value, LaunchValueRule rule)
    {
        var args = option == "--server" ? new[] { option, value } : new[] { "--server", "server.example.com", option, value };

        var options = LaunchOptions.Parse(args);

        Assert.Equal(new LaunchError(LaunchErrorKind.InvalidValue, option, value.Trim(), rule), options.Error);
    }

    [Fact]
    public void A_value_too_long_is_refused()
    {
        Assert.Equal(LaunchValueRule.Name, LaunchOptions.Parse(["--server", "server.example.com", "--user", new string('a', LaunchValues.MaxNameLength + 1)]).Error?.Rule);
        Assert.Equal(LaunchValueRule.Arguments, LaunchOptions.Parse(["--server", "server.example.com", "--remoteapp", "||A", "--remoteapp-args", new string('a', LaunchValues.MaxArgumentsLength + 1)]).Error?.Rule);
        Assert.Equal(LaunchValueRule.Host, LaunchOptions.Parse(["--server", string.Join('.', Enumerable.Repeat(new string('a', 60), 5))]).Error?.Rule);
        Assert.Equal(LaunchValueRule.Host, LaunchOptions.Parse(["--server", new string('a', 64) + ".example.com"]).Error?.Rule);
    }

    [Theory]
    [InlineData("server.example.com")]
    [InlineData("SERVER01")]
    [InlineData("rds_01.example.com")]
    [InlineData("192.0.2.10")]
    [InlineData("2001:db8::10")]
    [InlineData("[2001:db8::10]")]
    [InlineData("::1")]
    [InlineData("serveur-é.example.com")]
    public void A_host_name_or_an_address_is_accepted(string host)
    {
        Assert.True(LaunchValues.IsHost(host));
        Assert.Equal(host, LaunchOptions.Parse(["--server", host]).Connection.Server);
    }

    [Theory]
    [InlineData("Notepad", false)]
    [InlineData("||Notepad", false)]
    [InlineData("Bloc-notes 2", false)]
    [InlineData(@"C:\Windows\notepad.exe", true)]
    [InlineData(@"%windir%\notepad.exe", true)]
    [InlineData(@"\\fileserver\apps\app.exe", true)]
    [InlineData("tools/app.exe", true)]
    public void A_program_is_a_path_when_it_has_a_separator_or_a_colon_and_an_alias_otherwise(string program, bool path)
    {
        Assert.True(LaunchValues.IsProgram(program));
        Assert.Equal(path, LaunchValues.IsProgramPath(program));
        Assert.Equal(path ? program : "||" + program.TrimStart('|'), LaunchValues.RdpProgram(program));
    }

    [Fact]
    public void Separators_of_lines_and_lone_surrogates_are_refused_in_every_text()
    {
        foreach (var bad in new[] { "a" + (char)0x2028 + "b", "a" + (char)0x2029 + "b", "a" + (char)0xD800 + "b", "a" + (char)0xDC00, "a\u0085b", " a", "a ", string.Empty })
        {
            Assert.False(LaunchValues.IsArguments(bad), bad);
            Assert.False(LaunchValues.IsName(bad), bad);
        }

        Assert.True(LaunchValues.IsName("emoji \U0001F600"));
    }

    [Fact]
    public void The_arguments_of_a_command_line_give_it_back()
    {
        var options = LaunchOptions.Parse(["--show", "--ask-credentials", "--server", "server.example.com", "--audio", "off", "--windows-keys", "remote", @"C:\rdp\a.rdp"]);

        var again = LaunchOptions.Parse(options.ToArguments());

        Assert.Equal(options.Connection, again.Connection);
        Assert.Equal(options with { }, again);
    }
}
