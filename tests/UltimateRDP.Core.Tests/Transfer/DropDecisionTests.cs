using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

public class DropDecisionTests
{
    private const string DragId = "drag1";
    private const string TransferId = "transfer1";

    [Fact]
    public void A_new_decision_waits_and_sends_nothing()
    {
        var decision = new DropDecision(DragId, 1000);

        Assert.Equal(DropDecisionState.Waiting, decision.State);
        Assert.False(decision.SendsDrop);
        Assert.Null(decision.Deferred);
        Assert.Null(decision.DecidedAt);
        Assert.Equal(1500, decision.RemainingMilliseconds(1000));
        Assert.Equal(200, decision.WaitedMilliseconds(1200));
    }

    /// <summary>
    /// The case seen in production with 0.4.4: the user released 282 ms after the drag
    /// entered, and the dragAck that offered the transfer came 53 ms after the release.
    /// </summary>
    [Fact]
    public void An_ack_with_a_transfer_defers_the_drop()
    {
        var decision = new DropDecision(DragId, 10_126);

        Assert.True(decision.OnAck(new DragAckMessage(DragId, true, Transfer: true, TransferId: TransferId), 10_179));

        Assert.Equal(DropDecisionState.Transfer, decision.State);
        Assert.True(decision.SendsDrop);
        Assert.True(decision.Deferred);
        Assert.Equal(TransferId, decision.TransferId);
        Assert.Equal(53, decision.WaitedMilliseconds(20_000));
        Assert.Contains(TransferId, decision.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_ack_without_a_transfer_sends_the_plain_drop()
    {
        var decision = new DropDecision(DragId, 0);

        Assert.True(decision.OnAck(new DragAckMessage(DragId, true), 40));

        Assert.Equal(DropDecisionState.Plain, decision.State);
        Assert.True(decision.SendsDrop);
        Assert.Null(decision.Deferred);
        Assert.Null(decision.TransferId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_transfer_without_an_id_counts_as_no_transfer(string? transferId)
    {
        var decision = new DropDecision(DragId, 0);

        Assert.True(decision.OnAck(new DragAckMessage(DragId, true, Transfer: true, TransferId: transferId), 40));

        Assert.Equal(DropDecisionState.Plain, decision.State);
        Assert.Null(decision.Deferred);
    }

    [Fact]
    public void A_refusal_sends_no_drop()
    {
        var decision = new DropDecision(DragId, 0);

        Assert.True(decision.OnAck(new DragAckMessage(DragId, false, "Not enough space."), 300));

        Assert.Equal(DropDecisionState.Refused, decision.State);
        Assert.False(decision.SendsDrop);
        Assert.Null(decision.Deferred);
    }

    [Fact]
    public void Without_an_ack_the_plain_drop_goes_out_after_the_wait()
    {
        var decision = new DropDecision(DragId, 5000);

        Assert.False(decision.Expire(6499));
        Assert.Equal(DropDecisionState.Waiting, decision.State);
        Assert.Equal(1, decision.RemainingMilliseconds(6499));

        Assert.True(decision.Expire(6500));
        Assert.Equal(DropDecisionState.TimedOut, decision.State);
        Assert.True(decision.SendsDrop);
        Assert.Null(decision.Deferred);
        Assert.Equal(0, decision.RemainingMilliseconds(7000));
        Assert.Equal(1500, decision.WaitedMilliseconds(9000));
    }

    [Fact]
    public void The_first_answer_decides_and_later_ones_are_ignored()
    {
        var decision = new DropDecision(DragId, 0);
        Assert.True(decision.OnAck(new DragAckMessage(DragId, true), 100));

        Assert.False(decision.OnAck(new DragAckMessage(DragId, true, Transfer: true, TransferId: TransferId), 200));
        Assert.False(decision.Expire(5000));
        Assert.False(decision.Abandon(5000));

        Assert.Equal(DropDecisionState.Plain, decision.State);
        Assert.Equal(100, decision.DecidedAt);
    }

    [Fact]
    public void An_ack_after_the_wait_changes_nothing()
    {
        var decision = new DropDecision(DragId, 0);
        Assert.True(decision.Expire(1500));

        Assert.False(decision.OnAck(new DragAckMessage(DragId, true, Transfer: true, TransferId: TransferId), 1600));

        Assert.Equal(DropDecisionState.TimedOut, decision.State);
        Assert.Null(decision.TransferId);
    }

    [Fact]
    public void An_ack_of_another_drag_is_ignored()
    {
        var decision = new DropDecision(DragId, 0);

        Assert.False(decision.OnAck(new DragAckMessage("drag2", true, Transfer: true, TransferId: TransferId), 50));

        Assert.Equal(DropDecisionState.Waiting, decision.State);
    }

    [Fact]
    public void An_abandoned_drop_sends_nothing()
    {
        var decision = new DropDecision(DragId, 0);

        Assert.True(decision.Abandon(700));

        Assert.Equal(DropDecisionState.Abandoned, decision.State);
        Assert.False(decision.SendsDrop);
        Assert.False(decision.OnAck(new DragAckMessage(DragId, true), 800));
        Assert.Equal(700, decision.WaitedMilliseconds(5000));
    }

    [Fact]
    public void A_drag_id_is_required()
    {
        Assert.Throws<ArgumentException>(() => new DropDecision(string.Empty, 0));
    }
}
