using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

public class TransferPathsTests
{
    private static readonly TransferItem[] Items =
    [
        new TransferItem("report.pdf", false),
        new TransferItem("Photos", true),
    ];

    [Theory]
    [InlineData(@"C:\Users\me\report.pdf", "report.pdf")]
    [InlineData(@"C:\Users\me\Photos\", "Photos")]
    [InlineData(@"D:\Photos", "Photos")]
    [InlineData(@"C:\", "Drive C")]
    [InlineData(@"e:\", "Drive E")]
    [InlineData("C:", "Drive C")]
    public void Items_are_named_like_their_staged_copies(string clientPath, string expected)
    {
        Assert.Equal(expected, TransferPaths.ItemName(clientPath));
    }

    [Theory]
    [InlineData("Photos", @"2026\a.jpg", @"Photos\2026\a.jpg")]
    [InlineData("Photos", "2026/a.jpg", @"Photos\2026\a.jpg")]
    [InlineData("Photos", "a.jpg", @"Photos\a.jpg")]
    public void Paths_in_a_folder_are_combined_with_backslashes(string item, string inFolder, string expected)
    {
        Assert.Equal(expected, TransferPaths.Combine(item, inFolder));
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("REPORT.PDF")]
    [InlineData(@"Photos\a.jpg")]
    [InlineData(@"Photos\2026\.hidden")]
    [InlineData(@"Photos\a b\c..d.txt")]
    [InlineData(@"Photos\COM10.txt")]
    [InlineData(@"Photos\CONSOLE")]
    [InlineData(@"Photos\LPT")]
    [InlineData(@"Photos\nul2.txt")]
    public void Allowed_paths(string path)
    {
        Assert.True(TransferPaths.IsAllowed(path, Items));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(@"report.pdf\x")]
    [InlineData("Photos")]
    [InlineData(@"Other\a.jpg")]
    [InlineData(@"Photos\..\..\Windows\a.dll")]
    [InlineData(@"Photos\.\a.jpg")]
    [InlineData(@"Photos\\a.jpg")]
    [InlineData(@"Photos\")]
    [InlineData(@"\Photos\a.jpg")]
    [InlineData(@"C:\Photos\a.jpg")]
    [InlineData("Photos/a.jpg")]
    [InlineData(@"Photos\a:b.jpg")]
    [InlineData(@"Photos\a?.jpg")]
    [InlineData(@"Photos\a*.jpg")]
    [InlineData(@"Photos\a<b")]
    [InlineData(@"Photos\a|b")]
    [InlineData("Photos\\a\"b")]
    [InlineData("Photos\\a\tb")]
    [InlineData(@"Photos\a.")]
    [InlineData(@"Photos\a ")]
    [InlineData(@"Photos\NUL")]
    [InlineData(@"Photos\nul.txt")]
    [InlineData(@"Photos\COM1.log")]
    [InlineData(@"Photos\LPT9")]
    [InlineData(@"Photos\COM0")]
    [InlineData(@"Photos\lpt0.txt")]
    [InlineData(@"Photos\COM¹")]
    [InlineData(@"Photos\lpt³.log")]
    [InlineData(@"Photos\CONIN$")]
    [InlineData(@"Photos\conout$.txt")]
    [InlineData(@"Photos\a.txt:hidden")]
    [InlineData(@"Photos\a.txt::$DATA")]
    public void Refused_paths(string? path)
    {
        Assert.False(TransferPaths.IsAllowed(path, Items));
    }

    [Fact]
    public void A_path_longer_than_the_limit_is_refused()
    {
        var path = @"Photos\" + string.Join('\\', Enumerable.Repeat(new string('a', 200), 21));

        Assert.True(path.Length > TransferPaths.MaxLength);
        Assert.False(TransferPaths.IsAllowed(path, Items));
    }

    [Fact]
    public void A_name_longer_than_255_characters_is_refused()
    {
        Assert.False(TransferPaths.IsValidName(new string('a', 256)));
        Assert.True(TransferPaths.IsValidName(new string('a', 255)));
    }
}
