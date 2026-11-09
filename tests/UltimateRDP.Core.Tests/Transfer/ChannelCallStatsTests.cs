using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

public class ChannelCallStatsTests
{
    private const long TicksPerSecond = 1000;

    [Fact]
    public void Calls_are_counted_and_timed()
    {
        var stats = new ChannelCallStats();
        stats.Record(200_000, 100, 110);
        stats.Record(150_000, 200, 230);
        stats.Record(500, 300, 300);

        Assert.Equal(3, stats.Calls);
        Assert.Equal(350_500, stats.Characters);
        Assert.Equal(40, stats.TotalTicks);
        Assert.Equal(30, stats.MaxTicks);
        Assert.Equal(200, stats.SpanTicks);
    }

    [Fact]
    public void The_summary_gives_the_share_of_the_time_inside_the_calls()
    {
        var stats = new ChannelCallStats();
        stats.Record(200_000, 0, 600);
        stats.Record(200_000, 1000, 1400);

        Assert.Equal(
            "Transfer t1 channel calls: 2 calls, 400000 characters, 1000 ms inside SendOnVirtualChannel, 500.00 ms per call on average, 600.00 ms at most; 71 % of the 1400 ms from the first call to the last, 50 % of the 2000 ms of the transfer.",
            stats.Describe("t1", TicksPerSecond, 2000));
    }

    [Fact]
    public void Without_calls_the_summary_says_zero()
    {
        Assert.Equal(
            "Transfer t1 channel calls: 0 calls, 0 characters, 0 ms inside SendOnVirtualChannel, 0.00 ms per call on average, 0.00 ms at most; 0 % of the 0 ms from the first call to the last, 0 % of the 500 ms of the transfer.",
            new ChannelCallStats().Describe("t1", TicksPerSecond, 500));
    }
}
