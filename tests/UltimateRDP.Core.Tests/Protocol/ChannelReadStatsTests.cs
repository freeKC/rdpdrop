using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Tests.Protocol;

public class ChannelReadStatsTests
{
    /// <summary>Ticks of one millisecond in the cases below.</summary>
    private const long TicksPerSecond = 1000;

    [Fact]
    public void Reads_are_sorted_by_size_and_timed()
    {
        var stats = new ChannelReadStats();
        stats.RecordRead(1600, 5);
        stats.RecordRead(1600, 5);
        stats.RecordRead(700, 2);
        stats.RecordRead(3200, 1, headers: 2);
        stats.RecordRead(0, 500);
        stats.RecordProcessing(30);
        stats.RecordHandling(20);

        var snapshot = stats.Snapshot();

        Assert.Equal(new ChannelReadSnapshot(4, 1, 7100, 2, 1, 1, 2, 513, 30, 20), snapshot);
        Assert.Equal(1775, snapshot.AverageBytesPerRead);
    }

    [Fact]
    public void The_difference_of_two_snapshots_is_what_happened_in_between()
    {
        var stats = new ChannelReadStats();
        stats.RecordRead(1600, 10);
        var start = stats.Snapshot();
        stats.RecordRead(1600, 4);
        stats.RecordRead(100, 6);
        stats.RecordProcessing(7);

        var during = stats.Snapshot() - start;

        Assert.Equal(new ChannelReadSnapshot(2, 0, 1700, 1, 1, 0, 0, 10, 7, 0), during);
    }

    [Fact]
    public void The_summary_gives_counts_sizes_and_times()
    {
        var snapshot = new ChannelReadSnapshot(1250, 3, 2_000_000, 1240, 10, 0, 0, 9500, 480, 320);

        var text = snapshot.Describe("t1", 1600, TicksPerSecond, 10_000, "wts");

        Assert.Equal(
            "Transfer t1 channel reads: 1250 reads with data and 3 empty, 2000000 bytes, 1600 bytes per read on average; 1240 of 1600 bytes, 10 shorter, 0 longer; blocked in the read 9500 ms, processing 480 ms (320 ms handling messages), over 10000 ms; read mode wts.",
            text);
    }

    [Fact]
    public void The_summary_counts_the_headers_when_there_were_some()
    {
        var snapshot = new ChannelReadSnapshot(2, 0, 3200, 2, 0, 0, 2, 0, 0, 0);

        Assert.Contains(", 2 channel headers taken out;", snapshot.Describe("t1", 1600, TicksPerSecond, 1, "file with headers"));
    }

    [Fact]
    public void Without_reads_the_average_is_zero()
    {
        Assert.Equal(0, new ChannelReadSnapshot().AverageBytesPerRead);
    }
}
