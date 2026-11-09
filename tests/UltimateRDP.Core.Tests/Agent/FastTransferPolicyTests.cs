using UltimateRDP.Core.Agent;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Tests.Agent;

public class FastTransferPolicyTests
{
    private static DropItem File(long? size) => new(@"C:\a.bin", false, size);

    [Theory]
    [InlineData(null, FastTransferMode.Auto, true)]
    [InlineData("", FastTransferMode.Auto, true)]
    [InlineData("auto", FastTransferMode.Auto, true)]
    [InlineData(" Always ", FastTransferMode.Always, true)]
    [InlineData("NEVER", FastTransferMode.Never, true)]
    [InlineData("fast", FastTransferMode.Auto, false)]
    public void The_setting_is_parsed(string? value, FastTransferMode expected, bool recognized)
    {
        Assert.Equal(expected, FastTransferPolicy.Parse(value, out var wasRecognized));
        Assert.Equal(recognized, wasRecognized);
    }

    [Theory]
    [InlineData(FastTransferMode.Auto, "auto")]
    [InlineData(FastTransferMode.Always, "always")]
    [InlineData(FastTransferMode.Never, "never")]
    public void The_setting_is_written(FastTransferMode mode, string expected)
    {
        Assert.Equal(expected, FastTransferPolicy.ToSetting(mode));
        Assert.Equal(mode, FastTransferPolicy.Parse(expected, out _));
    }

    [Fact]
    public void The_default_threshold_is_ten_mebibytes()
    {
        Assert.Equal(10_485_760, FastTransferPolicy.DefaultMinBytes);
    }

    [Theory]
    [InlineData(10_485_759, false)]
    [InlineData(10_485_760, true)]
    [InlineData(5_000_000_000, true)]
    public void Auto_transfers_from_the_threshold(long size, bool expected)
    {
        Assert.Equal(expected, FastTransferPolicy.ShouldTransfer(FastTransferMode.Auto, FastTransferPolicy.DefaultMinBytes, [File(size)]));
    }

    [Fact]
    public void Auto_adds_the_sizes_of_every_item()
    {
        DropItem[] items = [File(6_000_000), new DropItem(@"C:\Folder", true, 5_000_000), File(null)];

        Assert.Equal(11_000_000, FastTransferPolicy.TotalSize(items));
        Assert.True(FastTransferPolicy.ShouldTransfer(FastTransferMode.Auto, FastTransferPolicy.DefaultMinBytes, items));
    }

    [Fact]
    public void Items_without_size_count_as_empty()
    {
        Assert.False(FastTransferPolicy.ShouldTransfer(FastTransferMode.Auto, FastTransferPolicy.DefaultMinBytes, [File(null), File(null)]));
    }

    [Fact]
    public void Always_and_never_ignore_the_size()
    {
        Assert.True(FastTransferPolicy.ShouldTransfer(FastTransferMode.Always, FastTransferPolicy.DefaultMinBytes, [File(1)]));
        Assert.True(FastTransferPolicy.ShouldTransfer(FastTransferMode.Always, FastTransferPolicy.DefaultMinBytes, []));
        Assert.False(FastTransferPolicy.ShouldTransfer(FastTransferMode.Never, 0, [File(long.MaxValue)]));
    }

    [Fact]
    public void A_huge_total_does_not_overflow()
    {
        Assert.Equal(long.MaxValue, FastTransferPolicy.TotalSize([File(long.MaxValue), File(long.MaxValue)]));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(15, 17)]
    [InlineData(1000, 1100)]
    [InlineData(10_485_760, 11_534_336)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public void The_free_space_needs_ten_percent_more(long bytes, long expected)
    {
        Assert.Equal(expected, FastTransferPolicy.RequiredFreeSpace(bytes));
    }
}
