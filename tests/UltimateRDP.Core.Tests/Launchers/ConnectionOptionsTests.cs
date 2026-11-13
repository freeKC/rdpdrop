using UltimateRDP.Core.Launchers;
using UltimateRDP.Core.Local;
using UltimateRDP.Core.RdpFiles;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Core.Tests.Launchers;

/// <summary>The options of a connection as keys of a .rdp file, since 0.10.0.</summary>
public class ConnectionOptionsTests
{
    private const string BaseFile = "full address:s:old.example.com:3390\r\nusername:s:old\r\nredirectclipboard:i:1\r\nredirectdrives:i:0\r\naudiomode:i:2\r\nscreen mode id:i:2\r\n";

    [Fact]
    public void A_connection_without_a_file_gives_a_file_the_parser_reads()
    {
        var connection = new ConnectionOptions
        {
            Server = "server.example.com",
            User = "jdupont",
            Domain = "EXAMPLE",
            RemoteApp = "Notepad",
            RemoteAppName = "Bloc-notes",
            RemoteAppArgs = "C:\\file 1.txt",
            Gateway = "gateway.example.com",
            Multimon = false,
            Scale = 200,
            WindowsKeys = WindowsKeysTarget.Remote,
            Clipboard = false,
            Drives = true,
            Printers = false,
            Audio = AudioTarget.Off,
        };

        var file = RdpFile.Parse(connection.ToRdpContent(null));

        Assert.Equal("server.example.com", file.FullAddress);
        Assert.Equal(3389, file.ServerPort);
        Assert.Equal("jdupont", file.UserName);
        Assert.Equal("EXAMPLE", file.Domain);
        Assert.True(file.RemoteApplicationMode);
        Assert.Equal("||Notepad", file.RemoteApplicationProgram);
        Assert.Equal("Bloc-notes", file.RemoteApplicationName);
        Assert.Equal("C:\\file 1.txt", file.RemoteApplicationCmdLine);
        Assert.Equal("gateway.example.com", file.GatewayHostname);
        Assert.Equal(1, file.GatewayUsageMethod);
        Assert.Equal(1, file.GatewayProfileUsageMethod);
        Assert.False(file.UseMultimon);
        Assert.Equal(200, file.DesktopScaleFactor);
        Assert.Equal(1, file.KeyboardHook);
        Assert.False(file.RedirectClipboard);
        Assert.True(file.RequestsDriveRedirection);
        Assert.Equal("*", file.DrivesToRedirect);
        Assert.False(file.RedirectPrinters);
        Assert.Equal(2, file.AudioMode);
        Assert.Equal(connection with { Port = 3389, RemoteApp = "||Notepad" }, ConnectionOptions.FromRdpFile(file));
    }

    [Fact]
    public void The_options_win_over_the_keys_of_the_file()
    {
        var connection = new ConnectionOptions { User = "jdupont", Clipboard = false, Drives = true, Audio = AudioTarget.Local };

        var file = RdpFile.Parse(connection.ToRdpContent(BaseFile));

        Assert.Equal("old.example.com:3390", file.FullAddress);
        Assert.Equal("jdupont", file.UserName);
        Assert.False(file.RedirectClipboard);
        Assert.True(file.RequestsDriveRedirection);
        Assert.Equal(0, file.AudioMode);
        Assert.Equal(2, file.ScreenModeId);
    }

    [Fact]
    public void A_port_alone_keeps_the_host_of_the_file()
    {
        var file = RdpFile.Parse(new ConnectionOptions { Port = 3391 }.ToRdpContent(BaseFile));

        Assert.Equal("old.example.com", file.FullAddress);
        Assert.Equal(3391, file.ServerPort);
        Assert.Equal(("old.example.com", (int?)null), RdpAddress.Split(file.FullAddress!));
    }

    [Fact]
    public void A_server_replaces_the_address_of_the_file_with_the_default_port()
    {
        var file = RdpFile.Parse(new ConnectionOptions { Server = "[2001:db8::10]" }.ToRdpContent(BaseFile));

        Assert.Equal("[2001:db8::10]", file.FullAddress);
        Assert.Equal(3389, file.ServerPort);
        Assert.Equal("2001:db8::10", RdpAddress.Split(file.FullAddress!).Host);
    }

    [Fact]
    public void No_drive_writes_an_empty_list_of_drives()
    {
        var file = RdpFile.Parse(new ConnectionOptions { Drives = false }.ToRdpContent("drivestoredirect:s:*\r\nredirectdrives:i:1"));

        Assert.False(file.RequestsDriveRedirection);
    }

    [Fact]
    public void A_path_as_program_is_written_as_it_is()
    {
        var file = RdpFile.Parse(new ConnectionOptions { Server = "server.example.com", RemoteApp = @"C:\Program Files\App\app.exe" }.ToRdpContent(null));

        Assert.Equal(@"C:\Program Files\App\app.exe", file.RemoteApplicationProgram);
        Assert.Equal("app.exe", RemoteAppLaunch.FromFile(file, out _)?.DisplayName);
    }

    [Fact]
    public void A_file_gives_only_the_keys_it_sets()
    {
        var options = ConnectionOptions.FromRdpFile(RdpFile.Parse("full address:s:server.example.com\r\nusername:s:  \r\ngatewayhostname:s:gateway.example.com\r\ngatewayusagemethod:i:0\r\nremoteapplicationmode:i:1\r\nremoteapplicationfile:s:C:\\a.txt"));

        Assert.Equal(new ConnectionOptions { Server = "server.example.com" }, options);
    }

    [Fact]
    public void An_open_request_carries_the_connection_and_refuses_a_wrong_one()
    {
        var good = new OpenRequest(null, LaunchWindow.Auto, true, new ConnectionOptions { Server = "server.example.com", RemoteApp = "||Calc", Audio = AudioTarget.Remote });
        var bad = good with { Connection = good.Connection! with { Server = "server.example.com\r\nusername:s:x" } };
        var noServer = good with { Connection = new ConnectionOptions { User = "jdupont" } };

        Assert.Equal(good, LocalMessages.DecodeOpen(LocalMessages.Encode(good)));
        Assert.Null(LocalMessages.DecodeOpen(LocalMessages.Encode(bad)));
        Assert.Null(LocalMessages.DecodeOpen(LocalMessages.Encode(noServer)));
        Assert.NotNull(LocalMessages.DecodeOpen(LocalMessages.Encode(noServer with { Path = @"C:\rdp\a.rdp" })));
        Assert.Equal(new OpenRequest(null, LaunchWindow.Show, false), LocalMessages.DecodeOpen("""{"path":null,"window":"show","askCredentials":false}"""u8.ToArray()));
    }

    [Fact]
    public void The_line_break_of_a_value_cannot_add_a_key()
    {
        var connection = new ConnectionOptions { Server = "server.example.com", User = "a\r\nfull address:s:evil.example.com" };

        Assert.Equal(LaunchErrorKind.InvalidValue, connection.Check(hasFile: false)?.Kind);
    }
}
