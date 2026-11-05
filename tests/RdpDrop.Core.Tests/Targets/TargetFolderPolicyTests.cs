using RdpDrop.Core.Targets;

namespace RdpDrop.Core.Tests.Targets;

public class TargetFolderPolicyTests
{
    private const string DesktopPath = @"C:\Users\me\Desktop";
    private const string DownloadsPath = @"C:\Users\me\Downloads";
    private const string CustomPath = @"D:\Inbox";

    private static readonly FallbackFolder DesktopFallback = new(FallbackKind.Desktop);
    private static readonly FallbackFolder DownloadsFallback = new(FallbackKind.Downloads);
    private static readonly FallbackFolder CustomFallback = new(FallbackKind.Custom, CustomPath);

    private static string Resolve(CursorTarget target, FallbackFolder fallback) =>
        TargetFolderPolicy.Resolve(target, fallback, DesktopPath, DownloadsPath);

    public static TheoryData<FallbackFolder> AllFallbacks => [DesktopFallback, DownloadsFallback, CustomFallback];

    [Theory]
    [MemberData(nameof(AllFallbacks))]
    public void Explorer_folder_is_used_whatever_the_fallback(FallbackFolder fallback)
    {
        var target = new CursorTarget(CursorTargetKind.ExplorerFolder, @"C:\Projects\Reports");

        Assert.Equal(@"C:\Projects\Reports", Resolve(target, fallback));
    }

    [Theory]
    [InlineData(@"\\fileserver\share\team")]
    [InlineData(@"\\tsclient\C\Users")]
    [InlineData(@"E:\")]
    public void Explorer_folder_on_share_or_drive_root_is_used(string folder)
    {
        Assert.Equal(folder, Resolve(new CursorTarget(CursorTargetKind.ExplorerFolder, folder), DownloadsFallback));
    }

    [Theory]
    [MemberData(nameof(AllFallbacks))]
    public void Desktop_target_gives_desktop_whatever_the_fallback(FallbackFolder fallback)
    {
        Assert.Equal(DesktopPath, Resolve(new CursorTarget(CursorTargetKind.Desktop), fallback));
    }

    [Fact]
    public void Desktop_target_ignores_its_folder_path()
    {
        Assert.Equal(DesktopPath, Resolve(new CursorTarget(CursorTargetKind.Desktop, @"C:\Elsewhere"), DownloadsFallback));
    }

    [Theory]
    [InlineData(CursorTargetKind.VirtualFolder, "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    [InlineData(CursorTargetKind.VirtualFolder, null)]
    [InlineData(CursorTargetKind.Other, null)]
    [InlineData(CursorTargetKind.Other, @"C:\Ignored")]
    [InlineData(CursorTargetKind.ExplorerFolder, null)]
    [InlineData(CursorTargetKind.ExplorerFolder, "")]
    [InlineData(CursorTargetKind.ExplorerFolder, "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    [InlineData(CursorTargetKind.ExplorerFolder, "shell:RecycleBinFolder")]
    [InlineData(CursorTargetKind.ExplorerFolder, @"relative\folder")]
    public void Unusable_targets_use_each_fallback(CursorTargetKind kind, string? folder)
    {
        var target = new CursorTarget(kind, folder);

        Assert.Equal(DesktopPath, Resolve(target, DesktopFallback));
        Assert.Equal(DownloadsPath, Resolve(target, DownloadsFallback));
        Assert.Equal(CustomPath, Resolve(target, CustomFallback));
    }

    [Fact]
    public void Nothing_under_cursor_uses_default_fallback_desktop()
    {
        Assert.Equal(FallbackKind.Desktop, FallbackFolder.Default.Kind);
        Assert.Equal(DesktopPath, Resolve(new CursorTarget(CursorTargetKind.Other), FallbackFolder.Default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("::{645FF040-5081-101B-9F08-00AA002F954E}")]
    [InlineData("not a path")]
    public void Custom_fallback_without_file_system_path_gives_desktop(string? custom)
    {
        var fallback = new FallbackFolder(FallbackKind.Custom, custom);

        Assert.Equal(DesktopPath, Resolve(new CursorTarget(CursorTargetKind.Other), fallback));
    }

    [Fact]
    public void Custom_path_is_ignored_for_other_fallback_kinds()
    {
        var target = new CursorTarget(CursorTargetKind.Other);

        Assert.Equal(DownloadsPath, Resolve(target, new FallbackFolder(FallbackKind.Downloads, CustomPath)));
        Assert.Equal(DesktopPath, Resolve(target, new FallbackFolder(FallbackKind.Desktop, CustomPath)));
    }

    [Fact]
    public void Arguments_are_validated()
    {
        var target = new CursorTarget(CursorTargetKind.Other);

        Assert.Throws<ArgumentNullException>(() => TargetFolderPolicy.Resolve(null!, DesktopFallback, DesktopPath, DownloadsPath));
        Assert.Throws<ArgumentNullException>(() => TargetFolderPolicy.Resolve(target, null!, DesktopPath, DownloadsPath));
        Assert.ThrowsAny<ArgumentException>(() => TargetFolderPolicy.Resolve(target, DesktopFallback, "", DownloadsPath));
        Assert.ThrowsAny<ArgumentException>(() => TargetFolderPolicy.Resolve(target, DesktopFallback, DesktopPath, " "));
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"c:\Users", true)]
    [InlineData(@"C:/Users", true)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server\share\folder", true)]
    [InlineData(@"\\server", false)]
    [InlineData(@"\\server\", false)]
    [InlineData(@"\\?\C:\x", false)]
    [InlineData(@"\\.\pipe\x", false)]
    [InlineData(@"\\\x\y", false)]
    [InlineData(@"C:", false)]
    [InlineData(@"C:x", false)]
    [InlineData(@"\Users", false)]
    [InlineData(@"Users", false)]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsFileSystemPath_accepts_drive_and_share_paths_only(string? path, bool expected)
    {
        Assert.Equal(expected, TargetFolderPolicy.IsFileSystemPath(path));
    }
}
