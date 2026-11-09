using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Tests.Protocol;

public class SentDataRetentionTests
{
    private static QueuedLine Chunk(string transferId, long position) => new("x\n", MessageTypes.FileChunk, transferId, position);

    [Fact]
    public void A_send_without_chunk_is_kept_ten_seconds()
    {
        var retention = new SentDataRetention<int>();
        retention.Add(1, 100, 1000, null);

        Assert.Empty(retention.ReleaseExpired(10_999));
        Assert.Equal([1], retention.ReleaseExpired(11_000));
        Assert.Equal(0, retention.Count);
        Assert.Equal(0, retention.RetainedBytes);
    }

    [Fact]
    public void A_send_with_chunks_waits_for_its_acknowledgement_not_for_ten_seconds()
    {
        var retention = new SentDataRetention<int>();
        retention.Add(1, 100, 0, Chunk("t1", 32_768));
        retention.Add(2, 100, 0, null);

        Assert.Equal([2], retention.ReleaseExpired(10_000));
        Assert.Empty(retention.ReleaseExpired(59_999));
        Assert.Equal([1], retention.ReleaseExpired(60_000));
    }

    [Fact]
    public void An_acknowledgement_releases_the_sends_before_the_acknowledged_one()
    {
        var retention = new SentDataRetention<int>();
        retention.Add(1, 10, 0, null);
        retention.Add(2, 10, 0, Chunk("t1", 32_768));
        retention.Add(3, 10, 0, Chunk("t1", 10 * 32_768));
        retention.Add(4, 10, 0, null);
        retention.Add(5, 10, 0, Chunk("t1", 20 * 32_768));

        // The first chunk of send 3 arrived: sends 1 and 2 arrived in full, 3 maybe not.
        Assert.Equal([1, 2], retention.ReleaseAcknowledged("t1", 10 * 32_768));
        Assert.Empty(retention.ReleaseAcknowledged("t1", (19 * 32_768) + 1));
        Assert.Equal([3, 4], retention.ReleaseAcknowledged("t1", 20 * 32_768));
        Assert.Equal(1, retention.Count);
        Assert.Equal(10, retention.RetainedBytes);
    }

    [Fact]
    public void An_acknowledgement_of_another_transfer_releases_nothing()
    {
        var retention = new SentDataRetention<int>();
        retention.Add(1, 10, 0, Chunk("t1", 100));
        retention.Add(2, 10, 0, Chunk("t1", 200));

        Assert.Empty(retention.ReleaseAcknowledged("t2", 1_000_000));
        Assert.Equal(2, retention.Count);
    }

    [Fact]
    public void Beyond_the_limit_the_oldest_sends_are_released_first()
    {
        var retention = new SentDataRetention<int>(maxRetainedBytes: 100);
        Assert.Empty(retention.Add(1, 40, 0, null));
        Assert.Empty(retention.Add(2, 40, 0, null));

        Assert.Equal([1], retention.Add(3, 40, 0, null));
        Assert.Equal(80, retention.RetainedBytes);

        // A send larger than the limit on its own is kept, alone.
        Assert.Equal([2, 3], retention.Add(4, 500, 0, null));
        Assert.Equal(1, retention.Count);
        Assert.Equal(500, retention.RetainedBytes);
    }

    [Fact]
    public void The_default_limit_is_sixty_four_mebibytes()
    {
        var retention = new SentDataRetention<int>();

        Assert.Equal(64L * 1024 * 1024, retention.MaxRetainedBytes);
        Assert.Equal(TimeSpan.FromSeconds(10), retention.HoldTime);
        Assert.Equal(TimeSpan.FromSeconds(60), retention.ChunkHoldTime);
    }

    [Fact]
    public void Release_all_gives_every_send_in_order()
    {
        var retention = new SentDataRetention<int>();
        retention.Add(1, 10, 0, null);
        retention.Add(2, 10, 0, Chunk("t1", 1));

        Assert.Equal([1, 2], retention.ReleaseAll());
        Assert.Equal(0, retention.Count);
        Assert.Equal(0, retention.RetainedBytes);
    }

    [Theory]
    [InlineData(4L * 1024 * 1024, SentDataRetention<int>.DefaultMaxRetainedBytes)]
    [InlineData(1L * 1024 * 1024, SentDataRetention<int>.DefaultMaxRetainedBytes)]
    [InlineData(32L * 1024 * 1024, 128L * 1024 * 1024)]
    public void The_limit_follows_the_window(long window, long expected)
    {
        Assert.Equal(expected, SentDataRetention<int>.LimitForWindow(window));
    }
}
