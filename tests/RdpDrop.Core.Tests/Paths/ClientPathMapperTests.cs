using RdpDrop.Core.Paths;

namespace RdpDrop.Core.Tests.Paths;

public class ClientPathMapperTests
{
    [Theory]
    [InlineData(@"C:\Users\me\a.txt", @"\\tsclient\C\Users\me\a.txt")]
    [InlineData(@"c:\Users\me\a.txt", @"\\tsclient\C\Users\me\a.txt")]
    [InlineData(@"d:\data", @"\\tsclient\D\data")]
    [InlineData(@"Z:\x\y", @"\\tsclient\Z\x\y")]
    [InlineData(@"C:\x\y", @"\\tsclient\C\x\y")]
    [InlineData(@"C:\", @"\\tsclient\C\")]
    [InlineData(@"c:/", @"\\tsclient\C\")]
    [InlineData(@"C:/Users/me/a.txt", @"\\tsclient\C\Users\me\a.txt")]
    [InlineData(@"C:\Users\\me\\\a.txt", @"\\tsclient\C\Users\me\a.txt")]
    [InlineData(@"C:\Users\me\folder\", @"\\tsclient\C\Users\me\folder")]
    [InlineData(@"C:\My Files\été 2026\résumé.docx", @"\\tsclient\C\My Files\été 2026\résumé.docx")]
    [InlineData(@"C:\a\.hidden\b..c", @"\\tsclient\C\a\.hidden\b..c")]
    [InlineData(@"\\?\C:\very\long\path", @"\\tsclient\C\very\long\path")]
    [InlineData(@"\\?\e:\x", @"\\tsclient\E\x")]
    public void Drive_paths_are_mapped(string clientPath, string expected)
    {
        var ok = ClientPathMapper.TryMapToTsClient(clientPath, out var mapped, out var reason);

        Assert.True(ok);
        Assert.Equal(expected, mapped);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData(@"\\server\share\a.txt")]
    [InlineData(@"\\server\share")]
    [InlineData(@"//server/share/a.txt")]
    [InlineData(@"\\tsclient\C\a.txt")]
    [InlineData(@"\\?\UNC\server\share\a.txt")]
    public void Unc_paths_are_rejected(string clientPath)
    {
        var ok = ClientPathMapper.TryMapToTsClient(clientPath, out var mapped, out var reason);

        Assert.False(ok);
        Assert.Equal(string.Empty, mapped);
        Assert.NotNull(reason);
        Assert.Contains("UNC", reason);
        Assert.Contains(clientPath, reason);
    }

    [Theory]
    [InlineData(@"a.txt")]
    [InlineData(@"folder\a.txt")]
    [InlineData(@".\a.txt")]
    [InlineData(@"..\a.txt")]
    [InlineData(@"\Users\me\a.txt")]
    [InlineData(@"1:\a.txt")]
    [InlineData(@"C")]
    public void Relative_paths_are_rejected(string clientPath)
    {
        var ok = ClientPathMapper.TryMapToTsClient(clientPath, out var mapped, out var reason);

        Assert.False(ok);
        Assert.Equal(string.Empty, mapped);
        Assert.NotNull(reason);
        Assert.Contains("relative", reason);
    }

    [Theory]
    [InlineData(@"C:a.txt")]
    [InlineData(@"C:")]
    [InlineData(@"d:folder\a.txt")]
    public void Drive_relative_paths_are_rejected(string clientPath)
    {
        var ok = ClientPathMapper.TryMapToTsClient(clientPath, out _, out var reason);

        Assert.False(ok);
        Assert.NotNull(reason);
        Assert.Contains("relative to the current folder of drive", reason);
    }

    [Theory]
    [InlineData(@"C:\a\..\b")]
    [InlineData(@"C:\..")]
    [InlineData(@"C:\a\.\b")]
    [InlineData(@"C:/a/../../Windows")]
    public void Dot_segments_are_rejected(string clientPath)
    {
        Assert.False(ClientPathMapper.TryMapToTsClient(clientPath, out _, out var reason));
        Assert.Contains("segment", reason);
    }

    [Theory]
    [InlineData(@"C:\a.txt:stream")]
    [InlineData(@"C:\a*b")]
    [InlineData(@"C:\a?b")]
    [InlineData("C:\\a\"b")]
    [InlineData(@"C:\a<b>")]
    [InlineData(@"C:\a|b")]
    [InlineData("C:\\a\u0001b")]
    public void Invalid_characters_are_rejected(string clientPath)
    {
        Assert.False(ClientPathMapper.TryMapToTsClient(clientPath, out _, out var reason));
        Assert.Contains("not allowed", reason);
    }

    [Theory]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\.\C:\a.txt")]
    public void Device_paths_are_rejected(string clientPath)
    {
        Assert.False(ClientPathMapper.TryMapToTsClient(clientPath, out _, out var reason));
        Assert.Contains("device path", reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_paths_are_rejected(string? clientPath)
    {
        Assert.False(ClientPathMapper.TryMapToTsClient(clientPath!, out var mapped, out var reason));
        Assert.Equal(string.Empty, mapped);
        Assert.Equal("The path is empty.", reason);
    }

    [Fact]
    public void Root_constant_is_tsclient()
    {
        Assert.Equal(@"\\tsclient\", ClientPathMapper.TsClientRoot);
    }

    [Theory]
    [InlineData(@"C:\Users\me\a.txt", PathRejection.None)]
    [InlineData(@"\\?\C:\a", PathRejection.None)]
    [InlineData(null, PathRejection.Empty)]
    [InlineData(" ", PathRejection.Empty)]
    [InlineData(@"\\.\C:\a.txt", PathRejection.DevicePath)]
    [InlineData(@"\\server\share\a.txt", PathRejection.NetworkPath)]
    [InlineData(@"\\?\UNC\server\share\a.txt", PathRejection.NetworkPath)]
    [InlineData(@"folder\a.txt", PathRejection.RelativePath)]
    [InlineData(@"C:a.txt", PathRejection.DriveRelativePath)]
    [InlineData(@"C:\a\..\b", PathRejection.DotSegment)]
    [InlineData(@"C:\a\b?c", PathRejection.InvalidCharacters)]
    public void Rejections_are_classified(string? clientPath, PathRejection expected)
    {
        Assert.Equal(expected, ClientPathMapper.Classify(clientPath, out var mapped));
        Assert.Equal(expected == PathRejection.None, mapped.Length > 0);
    }

    [Theory]
    [InlineData(@"C:\Users\me\a.txt")]
    [InlineData(@"\\server\share\a.txt")]
    [InlineData(@"C:a.txt")]
    [InlineData(@"\\?\d:x")]
    [InlineData(@"C:\a\..\b")]
    [InlineData("")]
    public void Classify_and_try_map_agree(string clientPath)
    {
        var rejection = ClientPathMapper.Classify(clientPath, out var classified);
        var ok = ClientPathMapper.TryMapToTsClient(clientPath, out var mapped, out var reason);

        Assert.Equal(rejection == PathRejection.None, ok);
        Assert.Equal(classified, mapped);
        Assert.Equal(ok ? null : ClientPathMapper.Describe(rejection, clientPath), reason);
    }

    [Fact]
    public void Drive_relative_reason_names_the_drive_after_a_long_path_prefix()
    {
        Assert.Contains("drive D:", ClientPathMapper.Describe(PathRejection.DriveRelativePath, @"\\?\d:x"));
        Assert.Equal(string.Empty, ClientPathMapper.Describe(PathRejection.None, @"C:\a"));
    }
}
