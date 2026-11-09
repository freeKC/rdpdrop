using UltimateRDP.Core.Agent;

namespace UltimateRDP.Core.Tests.Agent;

public class IdleReportTests
{
    private const long Hour = 3_600_000;

    [Fact]
    public void The_first_line_comes_one_interval_after_the_state_began()
    {
        var report = new IdleReport(TimeSpan.FromHours(1));

        Assert.False(report.IsDue(1_000));
        Assert.False(report.IsDue(1_000 + Hour - 1));
        Assert.True(report.IsDue(1_000 + Hour));
    }

    [Fact]
    public void Lines_are_at_most_one_per_interval()
    {
        var report = new IdleReport(TimeSpan.FromHours(1));
        report.IsDue(0);

        var lines = 0;
        for (long now = 0; now <= 5 * Hour; now += 10_000)
        {
            if (report.IsDue(now))
            {
                lines++;
            }
        }

        Assert.Equal(5, lines);
    }

    [Fact]
    public void Reset_starts_a_new_period()
    {
        var report = new IdleReport(TimeSpan.FromHours(1));
        report.IsDue(0);
        report.Reset();

        Assert.False(report.IsDue(Hour + 5));
        Assert.False(report.IsDue(2 * Hour));
        Assert.True(report.IsDue(2 * Hour + 5));
    }

    [Fact]
    public void A_zero_interval_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IdleReport(TimeSpan.Zero));
    }
}
