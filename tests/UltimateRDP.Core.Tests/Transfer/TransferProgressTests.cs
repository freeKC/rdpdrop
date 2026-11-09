using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

public class TransferProgressTests
{
    [Fact]
    public void The_percentage_is_rounded_down()
    {
        var progress = new TransferProgress(1000, 0);

        progress.Report(100, 429);

        Assert.Equal(42, progress.Percent);
    }

    [Fact]
    public void An_empty_transfer_is_complete()
    {
        Assert.Equal(100, new TransferProgress(0, 0).Percent);
    }

    [Fact]
    public void No_rate_before_half_a_second()
    {
        var progress = new TransferProgress(100_000_000, 0);

        progress.Report(400, 4_000_000);

        Assert.Null(progress.BytesPerSecond);
        Assert.Null(progress.Remaining);
    }

    [Fact]
    public void The_rate_and_the_time_left_follow_the_acks()
    {
        var progress = new TransferProgress(100_000_000, 0);

        progress.Report(1000, 10_000_000);
        progress.Report(2000, 20_000_000);

        Assert.Equal(10_000_000, progress.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(8), progress.Remaining);
    }

    [Fact]
    public void The_rate_covers_the_last_five_seconds()
    {
        var progress = new TransferProgress(1_000_000_000, 0);
        for (var second = 1; second <= 10; second++)
        {
            progress.Report(second * 1000L, second * 1_000_000L);
        }

        // Then the link becomes ten times faster.
        for (var second = 11; second <= 20; second++)
        {
            progress.Report(second * 1000L, 10_000_000 + ((second - 10) * 10_000_000L));
        }

        Assert.Equal(10_000_000, progress.BytesPerSecond);
    }

    [Fact]
    public void Done_bytes_never_go_back_or_past_the_total()
    {
        var progress = new TransferProgress(100, 0);

        progress.Report(10, 50);
        progress.Report(20, 40);
        Assert.Equal(50, progress.DoneBytes);

        progress.Report(30, 500);
        Assert.Equal(100, progress.DoneBytes);
    }

    [Fact]
    public void The_text_is_refreshed_at_most_four_times_a_second()
    {
        var progress = new TransferProgress(100, 0);

        Assert.True(progress.TryRefresh(1000));
        Assert.False(progress.TryRefresh(1100));
        Assert.False(progress.TryRefresh(1249));
        Assert.True(progress.TryRefresh(1250));
    }

    [Theory]
    [InlineData(0, TimeLeftUnit.Seconds, 0, 0, 5)]
    [InlineData(3, TimeLeftUnit.Seconds, 0, 0, 5)]
    [InlineData(41, TimeLeftUnit.Seconds, 0, 0, 45)]
    [InlineData(56, TimeLeftUnit.Minutes, 0, 1, 0)]
    [InlineData(60, TimeLeftUnit.Minutes, 0, 1, 0)]
    [InlineData(61, TimeLeftUnit.Minutes, 0, 2, 0)]
    [InlineData(3540, TimeLeftUnit.Minutes, 0, 59, 0)]
    [InlineData(3599, TimeLeftUnit.Hours, 1, 0, 0)]
    [InlineData(3600, TimeLeftUnit.Hours, 1, 0, 0)]
    [InlineData(3661, TimeLeftUnit.Hours, 1, 2, 0)]
    public void The_time_left_is_rounded_up(int seconds, TimeLeftUnit unit, int hours, int minutes, int roundedSeconds)
    {
        Assert.Equal(new TimeLeft(unit, hours, minutes, roundedSeconds), TimeLeft.From(TimeSpan.FromSeconds(seconds)));
    }
}
