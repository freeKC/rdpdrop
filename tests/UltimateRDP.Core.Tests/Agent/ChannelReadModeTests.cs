using UltimateRDP.Core.Agent;

namespace UltimateRDP.Core.Tests.Agent;

public class ChannelReadModeTests
{
    [Theory]
    [InlineData("wts", ChannelReadMode.Wts)]
    [InlineData("file", ChannelReadMode.File)]
    [InlineData(" FILE ", ChannelReadMode.File)]
    [InlineData("Wts", ChannelReadMode.Wts)]
    public void Known_values_are_read_in_any_case(string value, ChannelReadMode expected)
    {
        Assert.Equal(expected, ChannelReadModes.Parse(value, out var recognized));
        Assert.True(recognized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_missing_value_gives_wts_and_counts_as_recognized(string? value)
    {
        Assert.Equal(ChannelReadMode.Wts, ChannelReadModes.Parse(value, out var recognized));
        Assert.True(recognized);
    }

    [Theory]
    [InlineData("readfile")]
    [InlineData("1")]
    public void An_unknown_value_gives_wts_and_is_reported(string value)
    {
        Assert.Equal(ChannelReadMode.Wts, ChannelReadModes.Parse(value, out var recognized));
        Assert.False(recognized);
    }

    [Theory]
    [InlineData(ChannelReadMode.Wts, "wts")]
    [InlineData(ChannelReadMode.File, "file")]
    public void Setting_values_round_trip(ChannelReadMode mode, string text)
    {
        Assert.Equal(text, ChannelReadModes.ToSetting(mode));
        Assert.Equal(mode, ChannelReadModes.Parse(text, out _));
    }
}
