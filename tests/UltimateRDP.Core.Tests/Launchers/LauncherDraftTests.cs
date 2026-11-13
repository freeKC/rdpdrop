using UltimateRDP.Core.Launchers;
using UltimateRDP.Core.RdpFiles;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Core.Tests.Launchers;

/// <summary>The fields of the launcher generator, since 0.10.0.</summary>
public class LauncherDraftTests
{
    private const string RunningExe = @"C:\Users\user\AppData\Local\UltimateRDP\client-0.10.0\UltimateRDP.exe";

    [Fact]
    public void The_fields_give_a_launcher_with_trimmed_values_and_without_the_default_port()
    {
        var draft = new LauncherDraft { Server = " server.example.com ", Port = "3389", User = " jdupont ", RemoteApp = true, Program = " Notepad ", DisplayName = " ", AskPassword = true };

        var spec = draft.ToSpec(RunningExe, ["x"], out var error);

        Assert.Null(error);
        Assert.NotNull(spec);
        Assert.Equal(new ConnectionOptions { Server = "server.example.com", User = "jdupont", RemoteApp = "Notepad" }, spec.Options.Connection);
        Assert.True(spec.Options.AskCredentials);
        Assert.Equal(RunningExe, spec.Exe);
        Assert.Null(spec.RdpFileName);
    }

    [Fact]
    public void The_program_fields_are_left_out_for_a_desktop()
    {
        var draft = new LauncherDraft { Server = "server.example.com", Port = "3390", Program = "Notepad", DisplayName = "Bloc-notes", Arguments = "/x" };

        var connection = draft.ToConnection(out _);

        Assert.Equal(new ConnectionOptions { Server = "server.example.com", Port = 3390 }, connection);
        Assert.Equal("server.example.com", draft.EffectiveName);
    }

    [Theory]
    [InlineData("", "3389", false, "", LauncherField.Server, null)]
    [InlineData("server.example.com", "x", false, "", LauncherField.Port, LaunchValueRule.Port)]
    [InlineData("server.example.com", "70000", false, "", LauncherField.Port, LaunchValueRule.Port)]
    [InlineData("server.example.com", "3389", true, "", LauncherField.Program, null)]
    [InlineData("server.example.com", "3389", true, "a|b", LauncherField.Program, LaunchValueRule.Program)]
    [InlineData("bad host", "3389", false, "", LauncherField.Server, LaunchValueRule.Host)]
    public void A_wrong_field_is_named(string server, string port, bool remoteApp, string program, LauncherField field, LaunchValueRule? rule)
    {
        var draft = new LauncherDraft { Server = server, Port = port, RemoteApp = remoteApp, Program = program };

        Assert.Null(draft.ToSpec(RunningExe, [], out var error));
        Assert.Equal(new LauncherFieldError(field, rule), error);
    }

    [Fact]
    public void A_wrong_user_domain_gateway_or_path_is_named()
    {
        var draft = new LauncherDraft { Server = "server.example.com" };

        Assert.Equal(LauncherField.User, Error(draft with { User = "a\"b" })?.Field);
        Assert.Equal(LauncherField.Domain, Error(draft with { Domain = "--x" })?.Field);
        Assert.Equal(LauncherField.Gateway, Error(draft with { Gateway = "a b" })?.Field);
        Assert.Equal(LauncherField.DisplayName, Error(draft with { RemoteApp = true, Program = "||A", DisplayName = "a\"b" })?.Field);
        Assert.Equal(new LauncherFieldError(LauncherField.Exe, null, LauncherProblem.Exe), Error(draft with { ExeKind = LauncherExeKind.WithVariables, TypedExe = @"%LOCALAPPDATA\UltimateRDP.exe" }));
        Assert.Null(Error(draft with { ExeKind = LauncherExeKind.WithVariables, TypedExe = @"%LOCALAPPDATA%\UltimateRDP\UltimateRDP.exe" }));
        Assert.Null(Error(draft with { ExeKind = LauncherExeKind.NextToLauncher, TypedExe = "ignored\"" }));
    }

    [Theory]
    [InlineData(true, "Bloc-notes", "||Notepad", "server.example.com", "Bloc-notes")]
    [InlineData(true, "", "||Notepad", "server.example.com", "Notepad")]
    [InlineData(true, "", "Notepad", "server.example.com", "Notepad")]
    [InlineData(true, "", @"C:\Windows\System32\notepad.exe", "server.example.com", "notepad.exe")]
    [InlineData(false, "Bloc-notes", "||Notepad", "server.example.com", "server.example.com")]
    [InlineData(false, "", "", "", "UltimateRDP")]
    public void The_default_name_comes_from_the_program_or_the_server(bool remoteApp, string displayName, string program, string server, string name)
    {
        var draft = new LauncherDraft { RemoteApp = remoteApp, DisplayName = displayName, Program = program, Server = server };

        Assert.Equal(name, draft.DefaultName);
        Assert.Equal("Mine", (draft with { Name = " Mine " }).EffectiveName);
    }

    [Theory]
    [InlineData("Bloc-notes (server.example.com)", "Bloc-notes (server.example.com)")]
    [InlineData("a:b*c?d\"e<f>g|h%i/j\\k", "abcdefghijk")]
    [InlineData(" ..name.. ", "name")]
    [InlineData("%%%", "UltimateRDP")]
    public void The_file_name_keeps_only_what_a_file_and_a_launcher_can_hold(string name, string fileName)
    {
        var draft = new LauncherDraft { Name = name, Server = "server.example.com", WithRdpFile = true };

        Assert.Equal(fileName, draft.FileBaseName);
        Assert.Equal(fileName + ".rdp", draft.RdpFileName);
        Assert.True(LauncherBatch.IsRdpFileName(draft.RdpFileName));
        Assert.Equal(draft.RdpFileName, draft.ToSpec(RunningExe, [], out _)?.RdpFileName);
    }

    [Fact]
    public void A_file_fills_the_fields_of_its_connection_and_keeps_the_others()
    {
        var file = RdpFile.Parse("full address:s:server.example.com:3390\r\nusername:s:jdupont\r\nprompt for credentials:i:1\r\nremoteapplicationmode:i:1\r\nremoteapplicationprogram:s:||Notepad\r\nremoteapplicationname:s:Bloc-notes\r\naudiomode:i:1\r\nredirectclipboard:i:0");
        var current = new LauncherDraft { Name = "Old", Server = "old.example.com", Window = LaunchWindow.Show, ExeKind = LauncherExeKind.NextToLauncher, WithRdpFile = true };

        var draft = LauncherDraft.FromRdpFile(file, current);

        Assert.Equal(
            current with
            {
                Name = string.Empty,
                Server = "server.example.com",
                Port = "3390",
                User = "jdupont",
                AskPassword = true,
                RemoteApp = true,
                Program = "||Notepad",
                DisplayName = "Bloc-notes",
                Audio = AudioTarget.Remote,
                Clipboard = false,
            },
            draft);
        Assert.Equal("Bloc-notes", draft.EffectiveName);
    }

    private static LauncherFieldError? Error(LauncherDraft draft)
    {
        draft.ToSpec(RunningExe, [], out var error);
        return error;
    }
}
