using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

public class TransferAckWatchTests
{
    private const long Second = 1000;

    /// <summary>
    /// The second failure seen in production: the chunks never reached the agent, and the
    /// client only gave up after a minute. It now gives up ten seconds after the first chunk.
    /// </summary>
    [Fact]
    public void Missing_first_data_is_reported_after_ten_seconds_even_with_room_in_the_window()
    {
        var watch = new TransferAckWatch(0);
        watch.OnChunkSent(2 * Second);
        watch.OnChunkSent(3 * Second);

        Assert.Equal(TransferAckTimeout.None, watch.Check(12 * Second, waitingForAck: false));
        Assert.Equal(TransferAckTimeout.FirstData, watch.Check((12 * Second) + 1, waitingForAck: false));
        Assert.Equal(TransferAckTimeout.FirstData, watch.Check((12 * Second) + 1, waitingForAck: true));
        Assert.Equal(2 * Second, watch.FirstChunkSent);
    }

    [Fact]
    public void Before_the_first_chunk_only_the_long_rule_applies()
    {
        var watch = new TransferAckWatch(0);

        Assert.Equal(TransferAckTimeout.None, watch.Check(30 * Second, waitingForAck: true));
        Assert.Equal(TransferAckTimeout.Silent, watch.Check((60 * Second) + 1, waitingForAck: true));
        Assert.Equal(TransferAckTimeout.None, watch.Check(120 * Second, waitingForAck: false));
    }

    [Fact]
    public void After_the_first_ack_the_client_waits_a_minute_while_the_window_is_full()
    {
        var watch = new TransferAckWatch(0);
        watch.OnChunkSent(1 * Second);
        watch.OnAck(5 * Second);

        Assert.True(watch.HasAck);
        Assert.Equal(TransferAckTimeout.None, watch.Check(50 * Second, waitingForAck: true));
        Assert.Equal(TransferAckTimeout.None, watch.Check(65 * Second, waitingForAck: true));
        Assert.Equal(TransferAckTimeout.None, watch.Check(100 * Second, waitingForAck: false));
        Assert.Equal(TransferAckTimeout.Silent, watch.Check((65 * Second) + 1, waitingForAck: true));
    }

    [Fact]
    public void An_early_ack_disarms_the_first_data_rule()
    {
        var watch = new TransferAckWatch(0);
        watch.OnChunkSent(0);
        watch.OnAck(Second);

        Assert.Equal(TransferAckTimeout.None, watch.Check(30 * Second, waitingForAck: false));
    }

    [Fact]
    public void The_newest_line_goes_again_after_two_seconds_of_waiting_without_acknowledgement()
    {
        var watch = new TransferAckWatch(0);
        watch.OnChunkSent(1 * Second);
        watch.OnAck(2 * Second);

        Assert.False(watch.ProbeDue(4 * Second, waitingForAck: false));
        Assert.False(watch.ProbeDue((4 * Second) - 1, waitingForAck: true));
        Assert.True(watch.ProbeDue(4 * Second, waitingForAck: true));
        Assert.False(watch.ProbeDue(5 * Second, waitingForAck: true));
        Assert.True(watch.ProbeDue(6 * Second, waitingForAck: true));
        Assert.Equal(2, watch.Probes);

        watch.OnChunkSent(7 * Second);
        Assert.False(watch.ProbeDue((9 * Second) - 1, waitingForAck: true));
        Assert.True(watch.ProbeDue(9 * Second, waitingForAck: true));
    }

    [Fact]
    public void A_probe_does_not_delay_the_last_resort()
    {
        var watch = new TransferAckWatch(0);
        watch.OnChunkSent(0);
        watch.OnAck(1 * Second);
        for (var second = 3; second < 61; second += 2)
        {
            Assert.True(watch.ProbeDue(second * Second, waitingForAck: true));
        }

        Assert.Equal(TransferAckTimeout.Silent, watch.Check((61 * Second) + 1, waitingForAck: true));
    }
}
