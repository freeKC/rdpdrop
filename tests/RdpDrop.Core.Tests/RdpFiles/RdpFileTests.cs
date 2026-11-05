using System.Text;
using RdpDrop.Core.RdpFiles;

namespace RdpDrop.Core.Tests.RdpFiles;

public class RdpFileTests
{
    // Shape of a file saved by mstsc, plus gateway settings.
    private const string Sample = """
        screen mode id:i:1
        use multimon:i:0
        desktopwidth:i:1920
        desktopheight:i:1080
        session bpp:i:32
        full address:s:rdp.example.com:3390
        server port:i:3389
        username:s:CORP\jdoe
        domain:s:CORP
        audiomode:i:0
        redirectclipboard:i:1
        redirectdrives:i:0
        drivestoredirect:s:*
        prompt for credentials:i:0
        authentication level:i:2
        gatewayhostname:s:gw.example.com
        gatewayusagemethod:i:1
        gatewaycredentialssource:i:4
        gatewayprofileusagemethod:i:1
        promptcredentialonce:i:0
        """;

    private static void AssertSample(RdpFile file)
    {
        Assert.Equal("rdp.example.com:3390", file.FullAddress);
        Assert.Equal(3389, file.ServerPort);
        Assert.Equal(@"CORP\jdoe", file.UserName);
        Assert.Equal("CORP", file.Domain);
        Assert.Equal("gw.example.com", file.GatewayHostname);
        Assert.Equal(1, file.GatewayUsageMethod);
        Assert.Equal(4, file.GatewayCredentialsSource);
        Assert.Equal(1, file.GatewayProfileUsageMethod);
        Assert.Equal(2, file.AuthenticationLevel);
        Assert.False(file.PromptForCredentials);
        Assert.True(file.RedirectClipboard);
        Assert.False(file.RedirectDrives);
        Assert.Equal("*", file.DrivesToRedirect);
        Assert.True(file.RequestsDriveRedirection);
        Assert.Equal(1920, file.DesktopWidth);
        Assert.Equal(1080, file.DesktopHeight);
        Assert.Equal(1, file.ScreenModeId);
    }

    [Fact]
    public void Parse_reads_typed_properties()
    {
        AssertSample(RdpFile.Parse(Sample));
    }

    [Fact]
    public void Parse_handles_crlf_and_cr_line_endings()
    {
        AssertSample(RdpFile.Parse(Sample.ReplaceLineEndings("\r\n")));
        AssertSample(RdpFile.Parse(Sample.ReplaceLineEndings("\r")));
    }

    [Fact]
    public void FromBytes_reads_utf16_le_with_bom()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Sample.ReplaceLineEndings("\r\n"))).ToArray();
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);

        AssertSample(RdpFile.FromBytes(bytes));
    }

    [Fact]
    public void FromBytes_reads_utf16_be_with_bom()
    {
        var bytes = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(Sample)).ToArray();

        AssertSample(RdpFile.FromBytes(bytes));
    }

    [Fact]
    public void FromBytes_reads_utf16_le_without_bom()
    {
        AssertSample(RdpFile.FromBytes(Encoding.Unicode.GetBytes(Sample)));
    }

    [Fact]
    public void FromBytes_reads_utf8_with_bom()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Sample)).ToArray();

        AssertSample(RdpFile.FromBytes(bytes));
    }

    [Fact]
    public void FromBytes_reads_utf8_without_bom()
    {
        AssertSample(RdpFile.FromBytes(Encoding.UTF8.GetBytes(Sample)));
    }

    [Fact]
    public void FromBytes_keeps_non_ascii_text()
    {
        const string content = "username:s:Zoé\r\nfull address:s:café.example.com\r\n";

        Assert.Equal("Zoé", RdpFile.FromBytes(Encoding.UTF8.GetBytes(content)).UserName);
        Assert.Equal("Zoé", RdpFile.FromBytes([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(content)]).UserName);
    }

    [Fact]
    public void FromBytes_reads_empty_input()
    {
        Assert.Empty(RdpFile.FromBytes([]).Entries);
        Assert.Empty(RdpFile.FromBytes([0xFF, 0xFE]).Entries);
    }

    [Fact]
    public void Load_reads_utf16_file_from_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rdpdrop-{Guid.NewGuid():N}.rdp");
        try
        {
            File.WriteAllText(path, Sample.ReplaceLineEndings("\r\n"), Encoding.Unicode);

            AssertSample(RdpFile.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_reads_utf8_file_from_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rdpdrop-{Guid.NewGuid():N}.rdp");
        try
        {
            File.WriteAllText(path, Sample, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            AssertSample(RdpFile.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_throws_for_missing_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rdpdrop-missing-{Guid.NewGuid():N}.rdp");

        Assert.Throws<FileNotFoundException>(() => RdpFile.Load(path));
    }

    [Fact]
    public void Load_rejects_empty_path()
    {
        Assert.Throws<ArgumentException>(() => RdpFile.Load(""));
    }

    [Fact]
    public void Keys_are_case_insensitive()
    {
        var file = RdpFile.Parse("Full Address:s:host\nDESKTOPWIDTH:i:800\nRedirectDrives:I:1");

        Assert.Equal("host", file.GetString("full address"));
        Assert.Equal("host", file.GetString("FULL ADDRESS"));
        Assert.Equal("host", file.FullAddress);
        Assert.Equal(800, file.DesktopWidth);
        Assert.True(file.RedirectDrives);
        Assert.Equal('i', file.GetEntry("redirectdrives")!.Type);
    }

    [Fact]
    public void Values_may_contain_colons()
    {
        var file = RdpFile.Parse("full address:s:[fe80::1]:3389\nalternate shell:s:C:\\Windows\\notepad.exe\nempty:s:");

        Assert.Equal("[fe80::1]:3389", file.FullAddress);
        Assert.Equal(@"C:\Windows\notepad.exe", file.GetString("alternate shell"));
        Assert.Equal(string.Empty, file.GetString("empty"));
    }

    [Fact]
    public void Blank_lines_comments_and_malformed_lines_are_ignored()
    {
        const string content = """

            # comment:s:x
            ; other:s:y
            no colon at all
            onlyone:colon
            :s:no key
            bad type:x:1
            long type:ss:1
            missing type::1
               desktopwidth:i:1024

            """;

        var file = RdpFile.Parse(content);

        var entry = Assert.Single(file.Entries);
        Assert.Equal(new RdpFileEntry("desktopwidth", 'i', "1024"), entry);
        Assert.Null(file.GetString("# comment"));
        Assert.Null(file.GetString("bad type"));
    }

    [Fact]
    public void Last_duplicate_key_wins()
    {
        var file = RdpFile.Parse("desktopwidth:i:800\nDesktopWidth:i:1280");

        Assert.Equal(1280, file.DesktopWidth);
        Assert.Single(file.Entries);
    }

    [Fact]
    public void Missing_keys_return_null_or_default()
    {
        var file = RdpFile.Parse("");

        Assert.Null(file.FullAddress);
        Assert.Null(file.ServerPort);
        Assert.Null(file.UserName);
        Assert.Null(file.Domain);
        Assert.Null(file.GatewayHostname);
        Assert.Null(file.GatewayUsageMethod);
        Assert.Null(file.GatewayCredentialsSource);
        Assert.Null(file.GatewayProfileUsageMethod);
        Assert.Null(file.AuthenticationLevel);
        Assert.Null(file.DrivesToRedirect);
        Assert.Null(file.DesktopWidth);
        Assert.Null(file.DesktopHeight);
        Assert.Null(file.ScreenModeId);
        Assert.False(file.PromptForCredentials);
        Assert.True(file.RedirectClipboard);
        Assert.False(file.RedirectDrives);
        Assert.False(file.RequestsDriveRedirection);
        Assert.Null(file.GetEntry("anything"));
        Assert.Null(file.GetBinary("anything"));
    }

    [Theory]
    [InlineData("x:i:1", true)]
    [InlineData("x:i:0", false)]
    [InlineData("x:i:2", true)]
    [InlineData("x:i:-1", true)]
    [InlineData("x:i: 0 ", false)]
    public void GetBool_reads_integers(string line, bool expected)
    {
        var file = RdpFile.Parse(line);

        Assert.Equal(expected, file.GetBool("x", !expected));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("", false)]
    [InlineData("x:i:", true)]
    [InlineData("x:i:yes", false)]
    [InlineData("x:s:true", true)]
    public void GetBool_returns_default_when_absent_or_not_an_integer(string content, bool defaultValue)
    {
        Assert.Equal(defaultValue, RdpFile.Parse(content).GetBool("x", defaultValue));
    }

    [Theory]
    [InlineData("x:i:42", 42)]
    [InlineData("x:i:-7", -7)]
    [InlineData("x:i:+3", 3)]
    [InlineData("x:s:15", 15)]
    [InlineData("x:i:abc", null)]
    [InlineData("x:i:1.5", null)]
    [InlineData("x:i:99999999999", null)]
    [InlineData("x:i:", null)]
    [InlineData("", null)]
    public void GetInt_parses_integers(string content, int? expected)
    {
        Assert.Equal(expected, RdpFile.Parse(content).GetInt("x"));
    }

    [Fact]
    public void GetString_returns_value_whatever_the_type()
    {
        var file = RdpFile.Parse("a:i:12\nb:b:0A0B");

        Assert.Equal("12", file.GetString("a"));
        Assert.Equal("0A0B", file.GetString("b"));
    }

    [Fact]
    public void GetBinary_decodes_hex()
    {
        var file = RdpFile.Parse("blob:b:01ff7E\nbad:b:xyz\nodd:b:abc");

        Assert.Equal([0x01, 0xFF, 0x7E], file.GetBinary("blob"));
        Assert.Null(file.GetBinary("bad"));
        Assert.Null(file.GetBinary("odd"));
        Assert.Equal('b', file.GetEntry("blob")!.Type);
    }

    [Fact]
    public void Type_letter_is_lowercased()
    {
        var file = RdpFile.Parse("a:S:text\nb:I:1\nc:B:00");

        Assert.Equal(['s', 'i', 'b'], file.Entries.Select(e => e.Type).Order().Reverse().ToArray());
    }

    [Theory]
    [InlineData("redirectdrives:i:1", true)]
    [InlineData("drivestoredirect:s:C:;D:;", true)]
    [InlineData("drivestoredirect:s:*", true)]
    [InlineData("drivestoredirect:s:", false)]
    [InlineData("redirectdrives:i:0", false)]
    [InlineData("redirectdrives:i:0\ndrivestoredirect:s:", false)]
    public void RequestsDriveRedirection_combines_both_settings(string content, bool expected)
    {
        Assert.Equal(expected, RdpFile.Parse(content).RequestsDriveRedirection);
    }

    [Fact]
    public void Clipboard_and_prompt_settings_are_read()
    {
        var file = RdpFile.Parse("redirectclipboard:i:0\nprompt for credentials:i:1");

        Assert.False(file.RedirectClipboard);
        Assert.True(file.PromptForCredentials);
    }

    [Fact]
    public void Key_lookup_trims_spaces()
    {
        var file = RdpFile.Parse("server port : i : 3390");

        Assert.Equal(3390, file.ServerPort);
        Assert.Equal(3390, file.GetInt(" server port "));
    }

    [Fact]
    public void Parse_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => RdpFile.Parse(null!));
    }

    [Fact]
    public void Key_constants_match_mstsc_names()
    {
        Assert.Equal("full address", RdpKeys.FullAddress);
        Assert.Equal("screen mode id", RdpKeys.ScreenModeId);
        Assert.Equal("drivestoredirect", RdpKeys.DrivesToRedirect);
        Assert.Equal("authentication level", RdpKeys.AuthenticationLevel);
    }
}
