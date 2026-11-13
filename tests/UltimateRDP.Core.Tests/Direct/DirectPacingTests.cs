using UltimateRDP.Core.Direct;

namespace UltimateRDP.Core.Tests.Direct;

/// <summary>The token bucket of <c>directMaxMegabytesPerSecond</c> and the rates of the summary lines, on a clock of the test.</summary>
public class DirectPacingTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void The_bucket_lets_a_burst_through_then_paces_to_the_limit()
    {
        long now = 0;
        var pacer = new DirectPacer(10_000_000, () => now, burstBytes: 2 * MiB);

        Assert.Equal(0, pacer.Take(MiB));
        Assert.Equal(0, pacer.Take(MiB));

        // Beyond the burst, each block waits the time the limit gives it.
        Assert.Equal(105, pacer.Take(MiB));
        now += 105;
        Assert.Equal(105, pacer.Take(MiB));
    }

    [Fact]
    public void A_long_run_through_the_pacer_keeps_the_limit()
    {
        long now = 0;
        var pacer = new DirectPacer(10_000_000, () => now);
        for (var i = 0; i < 100; i++)
        {
            now += pacer.Take(MiB);
        }

        // 100 MiB at 10 MB/s, less the first burst of two blocks.
        var expected = (100 - 2) * MiB / 10_000.0;
        Assert.InRange(now, expected - 5, expected + 105);
    }

    [Fact]
    public void Connections_share_the_debt_so_that_together_they_keep_the_limit()
    {
        long now = 0;
        var pacer = new DirectPacer(10_000_000, () => now, burstBytes: MiB);
        pacer.Take(MiB);

        // Four connections read a block each at the same time: each waits longer than the one before.
        long[] waits = [pacer.Take(MiB), pacer.Take(MiB), pacer.Take(MiB), pacer.Take(MiB)];

        Assert.Equal([105, 210, 315, 420], waits);
    }

    [Fact]
    public void A_pause_refills_the_bucket_up_to_the_burst_only()
    {
        long now = 0;
        var pacer = new DirectPacer(10_000_000, () => now, burstBytes: 2 * MiB);
        pacer.Take(2 * MiB);
        now += 60_000;

        Assert.Equal(0, pacer.Take(2 * MiB));
        Assert.True(pacer.Take(MiB) > 0);
    }

    [Fact]
    public void No_limit_gives_no_pacer()
    {
        Assert.Null(DirectPacer.ForMegabytesPerSecond(0, () => 0));
        Assert.Equal(150_000_000, DirectPacer.ForMegabytesPerSecond(150, () => 0)!.BytesPerSecond);
    }

    [Fact]
    public void The_meter_gives_the_rate_of_the_first_and_the_last_five_seconds()
    {
        long now = 1000;
        var meter = new DirectRateMeter(() => now);
        meter.Start();

        // 150 MB/s for 4 seconds, then 20 MB/s for 10 seconds, sampled every second.
        for (var second = 0; second < 14; second++)
        {
            meter.Add(second < 4 ? 150_000_000 : 20_000_000);
            now += 1000;
            var rate = meter.Sample();
            Assert.Equal(second < 4 ? 150_000_000 : 20_000_000, rate!.Value, 0.001);
        }

        meter.Stop();
        var rates = meter.Rates();

        Assert.Equal(14_000, meter.ElapsedMilliseconds);
        Assert.Equal(800_000_000 / 14.0, rates.Average, 0.001);
        Assert.Equal(124_000_000, rates.First, 0.001);
        Assert.Equal(20_000_000, rates.Last, 0.001);
    }

    [Fact]
    public void A_short_transfer_has_the_same_first_last_and_average_rate()
    {
        long now = 0;
        var meter = new DirectRateMeter(() => now);
        meter.Start();
        meter.Add(300_000_000);
        now += 2000;
        meter.Stop();

        var rates = meter.Rates();

        Assert.Equal(150_000_000, rates.Average, 0.001);
        Assert.Equal(150_000_000, rates.First, 0.001);
        Assert.Equal(150_000_000, rates.Last, 0.001);
        Assert.Null(meter.Sample());
    }

    [Fact]
    public void A_meter_that_never_started_reports_nothing()
    {
        var meter = new DirectRateMeter(() => 5);
        meter.Add(10);
        meter.Stop();

        Assert.Equal(default, meter.Rates());
        Assert.Equal(0, meter.ElapsedMilliseconds);
        Assert.Null(meter.Sample());
    }
}
