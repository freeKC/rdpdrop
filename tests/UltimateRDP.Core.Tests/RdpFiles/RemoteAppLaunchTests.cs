using System.Text;
using UltimateRDP.Core.RdpFiles;

namespace UltimateRDP.Core.Tests.RdpFiles;

public class RemoteAppLaunchTests
{
    // Shape of a RemoteApp file saved by the Remote Desktop web feed, with a published program.
    private const string PublishedProgram = """
        full address:s:apps.example.com
        remoteapplicationmode:i:1
        remoteapplicationprogram:s:||MyApp
        remoteapplicationname:s:My Application
        alternate shell:s:||MyApp
        disableremoteappcapscheck:i:1
        drivestoredirect:s:*
        """;

    private static RemoteAppLaunch? Read(string content, out RemoteAppRejection rejection) =>
        RemoteAppLaunch.FromFile(RdpFile.Parse(content), out rejection);

    [Fact]
    public void A_published_program_is_read_as_an_alias()
    {
        var launch = Read(PublishedProgram, out var rejection);

        Assert.Equal(RemoteAppRejection.None, rejection);
        Assert.NotNull(launch);
        Assert.Equal("||MyApp", launch.Program);
        Assert.True(launch.IsAlias);
        Assert.Equal("My Application", launch.Name);
        Assert.Equal("My Application", launch.DisplayName);
        Assert.Null(launch.Args);
        Assert.Null(launch.File);
        Assert.False(launch.ExpandArgs);
        Assert.False(launch.ExpandWorkingDir);
    }

    [Fact]
    public void Without_a_name_the_alias_names_the_program()
    {
        var launch = Read("remoteapplicationmode:i:1\nremoteapplicationprogram:s:||MyApp", out _);

        Assert.Equal("MyApp", launch!.DisplayName);
    }

    [Fact]
    public void A_full_path_is_not_an_alias_and_its_file_name_names_it()
    {
        var launch = Read(@"remoteapplicationmode:i:1
remoteapplicationprogram:s:C:\Program Files\Tools\tool.exe", out var rejection);

        Assert.Equal(RemoteAppRejection.None, rejection);
        Assert.Equal(@"C:\Program Files\Tools\tool.exe", launch!.Program);
        Assert.False(launch.IsAlias);
        Assert.Equal("tool.exe", launch.DisplayName);
    }

    [Theory]
    [InlineData("full address:s:host")]
    [InlineData("remoteapplicationmode:i:0\nremoteapplicationprogram:s:||MyApp")]
    [InlineData("remoteapplicationmode:s:yes\nremoteapplicationprogram:s:||MyApp")]
    public void A_mode_absent_or_zero_opens_a_desktop(string content)
    {
        Assert.Null(Read(content, out var rejection));
        Assert.Equal(RemoteAppRejection.None, rejection);
    }

    [Theory]
    [InlineData("remoteapplicationmode:i:1")]
    [InlineData("remoteapplicationmode:i:1\nremoteapplicationprogram:s:")]
    [InlineData("remoteapplicationmode:i:1\nremoteapplicationprogram:s:   \nremoteapplicationname:s:Name only")]
    public void A_mode_without_a_program_is_refused(string content)
    {
        Assert.Null(Read(content, out var rejection));
        Assert.Equal(RemoteAppRejection.NoProgram, rejection);
    }

    [Fact]
    public void A_file_alone_is_enough()
    {
        var launch = Read(@"remoteapplicationmode:i:1
remoteapplicationfile:s:D:\Shared\report.xlsx", out var rejection);

        Assert.Equal(RemoteAppRejection.None, rejection);
        Assert.Null(launch!.Program);
        Assert.Equal(@"D:\Shared\report.xlsx", launch.File);
        Assert.Equal("report.xlsx", launch.DisplayName);
    }

    [Fact]
    public void The_arguments_and_the_expansion_flags_are_read()
    {
        var launch = Read("""
            remoteapplicationmode:i:1
            remoteapplicationprogram:s:||MyApp
            remoteapplicationcmdline:s:/open %USERNAME% "a b"
            remoteapplicationexpandcmdline:i:1
            remoteapplicationexpandworkingdir:i:1
            """, out _);

        Assert.Equal("/open %USERNAME% \"a b\"", launch!.Args);
        Assert.True(launch.ExpandArgs);
        Assert.True(launch.ExpandWorkingDir);
    }

    [Fact]
    public void An_alias_of_only_the_prefix_is_not_an_alias()
    {
        var launch = Read("remoteapplicationmode:i:1\nremoteapplicationprogram:s:||", out _);

        Assert.False(launch!.IsAlias);
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-8")]
    public void The_keys_are_read_from_both_encodings(string encoding)
    {
        var text = PublishedProgram.Replace("\n", "\r\n", StringComparison.Ordinal);
        byte[] bytes = encoding == "utf-16"
            ? [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)]
            : Encoding.UTF8.GetBytes(text);

        var launch = RemoteAppLaunch.FromFile(RdpFile.FromBytes(bytes), out var rejection);

        Assert.Equal(RemoteAppRejection.None, rejection);
        Assert.Equal("||MyApp", launch!.Program);
        Assert.Equal("My Application", launch.DisplayName);
    }

    [Fact]
    public void The_file_exposes_the_typed_keys()
    {
        var file = RdpFile.Parse(PublishedProgram);

        Assert.True(file.RemoteApplicationMode);
        Assert.Equal("||MyApp", file.RemoteApplicationProgram);
        Assert.Equal("My Application", file.RemoteApplicationName);
        Assert.Null(file.RemoteApplicationCmdLine);
        Assert.Null(file.RemoteApplicationFile);
    }
}
