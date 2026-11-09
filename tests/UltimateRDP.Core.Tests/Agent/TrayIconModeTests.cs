using UltimateRDP.Core.Agent;

namespace UltimateRDP.Core.Tests.Agent;

public class TrayIconModeTests
{
    [Theory]
    [InlineData("auto", TrayIconMode.Auto)]
    [InlineData("always", TrayIconMode.Always)]
    [InlineData("never", TrayIconMode.Never)]
    [InlineData("ALWAYS", TrayIconMode.Always)]
    [InlineData(" Never ", TrayIconMode.Never)]
    [InlineData("Auto", TrayIconMode.Auto)]
    public void Known_values_are_read_in_any_case(string value, TrayIconMode expected)
    {
        Assert.Equal(expected, TrayIconModes.Parse(value, out var recognized));
        Assert.True(recognized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_value_gives_auto_and_counts_as_recognized(string? value)
    {
        Assert.Equal(TrayIconMode.Auto, TrayIconModes.Parse(value, out var recognized));
        Assert.True(recognized);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("true")]
    [InlineData("0")]
    [InlineData("alwayss")]
    public void An_unknown_value_gives_auto_and_is_reported(string value)
    {
        Assert.Equal(TrayIconMode.Auto, TrayIconModes.Parse(value, out var recognized));
        Assert.False(recognized);
    }

    [Theory]
    [InlineData(TrayIconMode.Auto, "auto")]
    [InlineData(TrayIconMode.Always, "always")]
    [InlineData(TrayIconMode.Never, "never")]
    public void Setting_values_round_trip(TrayIconMode mode, string text)
    {
        Assert.Equal(text, TrayIconModes.ToSetting(mode));
        Assert.Equal(mode, TrayIconModes.Parse(text, out _));
    }

    [Theory]
    [InlineData(TrayIconMode.Auto, false, false)]
    [InlineData(TrayIconMode.Auto, true, true)]
    [InlineData(TrayIconMode.Always, false, true)]
    [InlineData(TrayIconMode.Always, true, true)]
    [InlineData(TrayIconMode.Never, false, false)]
    [InlineData(TrayIconMode.Never, true, false)]
    public void Visibility_follows_the_mode_and_the_client(TrayIconMode mode, bool clientConnected, bool expected)
    {
        Assert.Equal(expected, TrayIconModes.IsVisible(mode, clientConnected));
    }
}
