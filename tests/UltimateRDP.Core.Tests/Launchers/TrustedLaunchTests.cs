using UltimateRDP.Core.Launchers;
using UltimateRDP.Core.Local;
using UltimateRDP.Core.RdpFiles;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Core.Tests.Launchers;

/// <summary><c>--trusted</c>, since 0.10.0: only the command line turns off the warning of Windows.</summary>
public class TrustedLaunchTests
{
    [Fact]
    public void Trusted_is_off_unless_the_command_line_asks_for_it()
    {
        Assert.False(LaunchOptions.Parse([@"C:\rdp\a.rdp"]).Trusted);
        Assert.True(LaunchOptions.Parse(["--TRUSTED", @"C:\rdp\a.rdp"]).Trusted);
        Assert.True(LaunchOptions.Parse(["--server", "server.example.com", "--trusted"]).Trusted);
    }

    [Fact]
    public void No_key_of_a_rdp_file_gives_options_that_turn_the_warning_off()
    {
        var file = RdpFile.Parse("full address:s:server.example.com\r\ntrusted:i:1\r\nshowredirectionwarningdialog:i:0\r\nredirectionwarningtype:i:5\r\nmarkrdpsettingssecure:i:1");

        var options = new LaunchOptions(null, LaunchWindow.Auto, false, null) { Connection = ConnectionOptions.FromRdpFile(file) };

        Assert.False(options.Trusted);
        Assert.False(LaunchOptions.Parse(options.ToArguments()).Trusted);
    }

    [Fact]
    public void A_launcher_and_a_second_start_carry_it()
    {
        var draft = new LauncherDraft { Server = "server.example.com", RemoteApp = true, Program = "||Notepad", Trusted = true };
        var spec = draft.ToSpec(@"C:\Tools\UltimateRDP.exe", [], out _)!;

        var (_, args) = CmdModel.Run(LauncherBatch.Build(spec), @"C:\Launchers\", new Dictionary<string, string>());
        var parsed = LaunchOptions.Parse(args);
        var request = new OpenRequest(null, parsed.Window, parsed.AskCredentials, parsed.Connection, parsed.Trusted);

        Assert.True(parsed.Trusted);
        Assert.Equal(request, LocalMessages.DecodeOpen(LocalMessages.Encode(request)));
        var plain = (draft with { Trusted = false }).ToSpec(@"C:\Tools\UltimateRDP.exe", [], out _)!;
        Assert.False(LaunchOptions.Parse(CmdModel.Run(LauncherBatch.Build(plain), @"C:\Launchers\", new Dictionary<string, string>()).Args).Trusted);
    }
}
