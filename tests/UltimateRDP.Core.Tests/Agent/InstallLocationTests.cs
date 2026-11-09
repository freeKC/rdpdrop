using UltimateRDP.Core.Agent;

namespace UltimateRDP.Core.Tests.Agent;

public class InstallLocationTests
{
    private static readonly string?[] Roots = [@"C:\Users\someone", @"C:\Users"];

    [Theory]
    [InlineData(@"C:\Users\someone\Downloads\UltimateRDP.Agent.exe")]
    [InlineData(@"C:\Users\other\Desktop\UltimateRDP.Agent.exe")]
    [InlineData(@"c:\users\SOMEONE\AppData\Local\Temp\UltimateRDP.Agent.exe")]
    [InlineData("C:/Users/someone/UltimateRDP.Agent.exe")]
    [InlineData("\"C:\\Users\\someone\\UltimateRDP.Agent.exe\"")]
    public void Paths_inside_a_profile_are_detected(string path)
    {
        Assert.True(InstallLocation.IsUnderAny(path, Roots));
    }

    [Theory]
    [InlineData(@"C:\Program Files\UltimateRDP\UltimateRDP.Agent.exe")]
    [InlineData(@"D:\Apps\UltimateRDP.Agent.exe")]
    [InlineData(@"C:\UsersData\UltimateRDP.Agent.exe")]
    [InlineData(@"C:\Users")]
    [InlineData("")]
    public void Paths_outside_the_profiles_are_accepted(string path)
    {
        Assert.False(InstallLocation.IsUnderAny(path, Roots));
    }

    [Fact]
    public void Roots_with_a_trailing_slash_or_empty_are_handled()
    {
        Assert.True(InstallLocation.IsUnderAny(@"E:\Profiles\me\a.exe", [null, "", @"E:\Profiles\"]));
        Assert.False(InstallLocation.IsUnderAny(@"E:\Profiles\me\a.exe", [null, ""]));
    }

    [Fact]
    public void The_suggested_folder_is_program_files()
    {
        Assert.Equal(@"C:\Program Files\UltimateRDP", InstallLocation.SuggestedFolder);
        Assert.False(InstallLocation.IsUnderAny(InstallLocation.SuggestedFolder + @"\UltimateRDP.Agent.exe", Roots));
    }
}
