using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

public class ReliableInboxTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>The lines of one file of three chunks: transferBegin, fileBegin, the chunks, fileEnd, numbered 0 to 5.</summary>
    private static Message[] Lines() =>
    [
        new TransferBeginMessage("t1", 1, 30, Seq: 0),
        new FileBeginMessage("t1", 0, "a.bin", 30, Seq: 1),
        new FileChunkMessage("t1", 0, 0, "AAAAAAAAAAAAAA==", Seq: 2),
        new FileChunkMessage("t1", 0, 10, "AAAAAAAAAAAAAA==", Seq: 3),
        new FileChunkMessage("t1", 0, 20, "AAAAAAAAAAAAAA==", Seq: 4),
        new FileEndMessage("t1", 0, Hash, Seq: 5),
    ];

    [Fact]
    public void Lines_in_order_are_handed_on_at_once()
    {
        var inbox = new ReliableInbox("t1");
        var lines = Lines();

        foreach (var line in lines)
        {
            var step = inbox.Accept(line, 0);
            Assert.Equal([line], step.Ready);
            Assert.Null(step.Request);
            Assert.False(step.Duplicate);
        }

        Assert.Equal(6, inbox.Expected);
        Assert.False(inbox.HasGap);
        Assert.Equal(0, inbox.Requests);
    }

    /// <summary>The incident of 0.5.0: the call of transferBegin and fileBegin was lost, the chunks came.</summary>
    [Fact]
    public void A_gap_is_named_once_and_the_lines_behind_it_follow_the_missing_ones()
    {
        var inbox = new ReliableInbox("t1");
        var lines = Lines();

        var first = inbox.Accept(lines[2], 0);
        var second = inbox.Accept(lines[3], 1);

        Assert.Empty(first.Ready);
        Assert.Equal(new TransferResendMessage("t1", 0, 1), first.Request);
        Assert.Empty(second.Ready);
        Assert.Null(second.Request);
        Assert.Equal((0L, 1L), inbox.Gap);
        Assert.Equal(2, inbox.HeldLines);
        Assert.True(inbox.HeldCharacters > 0);

        Assert.Equal([lines[0]], inbox.Accept(lines[0], 2).Ready);
        var filled = inbox.Accept(lines[1], 3);

        Assert.Equal([lines[1], lines[2], lines[3]], filled.Ready);
        Assert.Equal(2, filled.Released);
        Assert.Null(filled.Request);
        Assert.Equal(4, inbox.Expected);
        Assert.False(inbox.HasGap);
        Assert.Equal(0, inbox.HeldCharacters);
        Assert.Equal(1, inbox.Requests);
        Assert.Equal(2, inbox.ReleasedLines);
    }

    [Fact]
    public void Lines_that_already_arrived_are_ignored()
    {
        var inbox = new ReliableInbox("t1");
        var lines = Lines();
        inbox.Accept(lines[0], 0);
        inbox.Accept(lines[1], 0);
        inbox.Accept(lines[3], 0);

        var old = inbox.Accept(lines[0], 0);
        var held = inbox.Accept(lines[3], 0);

        Assert.True(old.Duplicate);
        Assert.Empty(old.Ready);
        Assert.True(held.Duplicate);
        Assert.Null(held.Request);
        Assert.Equal(2, inbox.DuplicateLines);
        Assert.Equal(1, inbox.HeldLines);
    }

    [Fact]
    public void The_request_goes_again_after_a_silence_of_half_a_second()
    {
        var inbox = new ReliableInbox("t1");
        var lines = Lines();
        inbox.Accept(lines[3], 1000);

        Assert.Null(inbox.Check(1499).Request);
        Assert.Equal(new TransferResendMessage("t1", 0, 2), inbox.Check(1500).Request);
        Assert.Null(inbox.Check(1999).Request);
        Assert.NotNull(inbox.Check(2000).Request);
        Assert.Equal(3, inbox.Requests);
    }

    /// <summary>Lines sent before the resend may still be on their way behind a full window: no request while they come.</summary>
    [Fact]
    public void Lines_that_keep_coming_hold_the_request_back()
    {
        var inbox = new ReliableInbox("t1");
        var lines = Lines();
        inbox.Accept(lines[2], 0);
        inbox.Accept(lines[3], 400);
        inbox.Accept(lines[4], 800);

        Assert.Null(inbox.Check(1200).Request);
        Assert.NotNull(inbox.Check(1300).Request);
    }

    [Fact]
    public void Lines_missing_after_ten_requests_fail_the_transfer()
    {
        var inbox = new ReliableInbox("t1");
        var lines = Lines();
        inbox.Accept(lines[2], 0);
        var now = 0L;
        while (inbox.Requests < ReliableInbox.MaxRequests)
        {
            now += 500;
            Assert.NotNull(inbox.Check(now).Request);
        }

        Assert.False(inbox.Check(now + 499).Failed);
        var step = inbox.Check(now + 500);

        Assert.True(step.Failed);
        Assert.Null(step.Request);
        Assert.Equal(InboxFailure.Unrecovered, inbox.Failure);
        Assert.Equal("lines 0 to 1 of the transfer never arrived after 10 requests to the client.", inbox.FailureDetail);
        Assert.False(inbox.HasGap);
        Assert.Empty(inbox.Accept(lines[0], now + 600).Ready);
    }

    [Fact]
    public void Progress_starts_the_count_of_requests_again()
    {
        var inbox = new ReliableInbox("t1");
        var lines = Lines();
        inbox.Accept(lines[4], 0);
        for (var i = 1; i < ReliableInbox.MaxRequests; i++)
        {
            inbox.Check(i * 500);
        }

        // Part of the missing lines arrives: the rest was already asked for, and is asked again after a silence.
        var part = inbox.Accept(lines[0], 5000);
        Assert.Null(part.Request);
        for (var i = 1; i <= ReliableInbox.MaxRequests; i++)
        {
            Assert.NotNull(inbox.Check(5000 + (i * 500)).Request);
        }

        Assert.Equal(InboxFailure.None, inbox.Failure);
    }

    [Fact]
    public void A_second_gap_is_asked_for_as_soon_as_the_first_one_is_filled()
    {
        var inbox = new ReliableInbox("t1");
        var lines = Lines();
        Assert.Equal(new TransferResendMessage("t1", 0, 1), inbox.Accept(lines[2], 0).Request);
        Assert.Null(inbox.Accept(lines[5], 0).Request);
        inbox.Accept(lines[0], 0);

        var step = inbox.Accept(lines[1], 0);

        Assert.Equal([lines[1], lines[2]], step.Ready);
        Assert.Equal(new TransferResendMessage("t1", 3, 4), step.Request);
    }

    [Fact]
    public void The_lines_waiting_are_bounded()
    {
        var inbox = new ReliableInbox("t1", maxHeldCharacters: 600);
        var lines = Lines();
        Assert.False(inbox.Accept(lines[2], 0).Failed);
        Assert.False(inbox.Accept(lines[3], 0).Failed);

        var step = inbox.Accept(lines[4], 0);

        Assert.True(step.Failed);
        Assert.Equal(InboxFailure.Overflow, inbox.Failure);
        Assert.Equal(0, inbox.HeldLines);
        Assert.Contains("waited behind line 0", inbox.FailureDetail, StringComparison.Ordinal);
    }

    /// <summary>The lines 0 and 1 handed on, then line 2 arrives damaged: the handler refuses it.</summary>
    private static (ReliableInbox Inbox, Message[] Lines, InboxStep Damaged) DamagedThirdLine()
    {
        var inbox = new ReliableInbox("t1");
        var lines = Lines();
        inbox.Accept(lines[0], 0, _ => true);
        inbox.Accept(lines[1], 0, _ => true);
        return (inbox, lines, inbox.Accept(lines[2], 10, _ => false));
    }

    [Fact]
    public void A_damaged_line_is_asked_for_at_once_and_the_lines_after_it_wait()
    {
        var (inbox, lines, damaged) = DamagedThirdLine();

        Assert.Empty(damaged.Ready);
        Assert.Equal(2, damaged.Damaged);
        Assert.Equal(new TransferResendMessage("t1", 2, 2, Damaged: true), damaged.Request);
        Assert.Equal(2, inbox.Expected);
        Assert.True(inbox.HasGap);
        Assert.Equal((2L, 2L), inbox.Gap);

        var later = inbox.Accept(lines[3], 20, _ => true);
        Assert.Empty(later.Ready);
        Assert.Null(later.Request);
        Assert.Equal((2L, 2L), inbox.Gap);

        List<Message> handled = [];
        var resent = inbox.Accept(lines[2], 30, line =>
        {
            handled.Add(line);
            return true;
        });

        Assert.Equal([lines[2], lines[3]], resent.Ready);
        Assert.Equal([lines[2], lines[3]], handled);
        Assert.Equal(1, resent.Released);
        Assert.Null(resent.Damaged);
        Assert.Null(resent.Request);
        Assert.False(inbox.HasGap);
        Assert.Null(inbox.Gap);
        Assert.Equal(1, inbox.Requests);
        Assert.Equal(1, inbox.DamagedRequests);
        Assert.Equal(1, inbox.DamagedLines);
        Assert.Equal(0, inbox.DuplicateLines);
    }

    [Fact]
    public void A_damaged_line_that_waited_is_dropped_when_its_turn_comes()
    {
        var inbox = new ReliableInbox("t1");
        var lines = Lines();
        inbox.Accept(lines[0], 0, _ => true);
        inbox.Accept(lines[1], 0, _ => true);
        Assert.Equal(new TransferResendMessage("t1", 2, 2), inbox.Accept(lines[3], 0, _ => true).Request);

        var filled = inbox.Accept(lines[2], 10, line => line != lines[3]);

        Assert.Equal([lines[2]], filled.Ready);
        Assert.Equal(0, filled.Released);
        Assert.Equal(3, filled.Damaged);
        Assert.Equal(new TransferResendMessage("t1", 3, 3, Damaged: true), filled.Request);
        Assert.Equal(0, inbox.HeldLines);
        Assert.Equal(0, inbox.HeldCharacters);
        Assert.Equal((3L, 3L), inbox.Gap);

        Assert.Empty(inbox.Accept(lines[4], 20, _ => true).Ready);
        Assert.Equal([lines[3], lines[4]], inbox.Accept(lines[3], 30, _ => true).Ready);
        Assert.Equal(2, inbox.Requests);
        Assert.Equal(1, inbox.DamagedRequests);
    }

    [Fact]
    public void A_damaged_resend_is_asked_for_again_after_the_silence()
    {
        var (inbox, lines, _) = DamagedThirdLine();

        var again = inbox.Accept(lines[2], 100, _ => false);

        Assert.Null(again.Request);
        Assert.Equal(2, again.Damaged);
        Assert.Null(inbox.Check(599).Request);
        Assert.Equal(new TransferResendMessage("t1", 2, 2, Damaged: true), inbox.Check(600).Request);
        Assert.Equal(2, inbox.Requests);
        Assert.Equal(2, inbox.DamagedRequests);
        Assert.Equal(2, inbox.DamagedLines);
    }

    [Fact]
    public void A_line_that_always_arrives_damaged_fails_after_ten_requests()
    {
        var (inbox, lines, _) = DamagedThirdLine();
        var now = 10L;
        for (var request = 2; request <= ReliableInbox.MaxRequests; request++)
        {
            now += 100;
            inbox.Accept(lines[2], now, _ => false);
            now += 500;
            Assert.Equal(new TransferResendMessage("t1", 2, 2, Damaged: true), inbox.Check(now).Request);
        }

        inbox.Accept(lines[2], now + 100, _ => false);
        var step = inbox.Check(now + 600);

        Assert.True(step.Failed);
        Assert.Equal(InboxFailure.Unrecovered, inbox.Failure);
        Assert.Equal(ReliableInbox.MaxRequests, inbox.Requests);
        Assert.Equal(ReliableInbox.MaxRequests, inbox.DamagedRequests);
        Assert.Equal("lines 2 to 2 of the transfer never arrived intact after 10 requests to the client; line 2 arrived damaged.", inbox.FailureDetail);
        Assert.False(inbox.HasGap);
        Assert.Null(inbox.Gap);
    }

    [Fact]
    public void The_default_bound_holds_twice_the_largest_window_in_base64()
    {
        Assert.Equal(TransferRegistry<object>.MaxQueuedCharacters, new ReliableInbox("t1").MaxHeldCharacters);
        Assert.True(ReliableInbox.DefaultMaxHeldCharacters > 2 * TransferSender.MaxWindowBytes * 4 / 3);
    }

    [Fact]
    public void Only_the_lines_of_a_transfer_carry_a_number()
    {
        Assert.Equal(3, ReliableInbox.SeqOf(new FileChunkMessage("t1", 0, 0, "AA==", Seq: 3)));
        Assert.Null(ReliableInbox.SeqOf(new FileChunkMessage("t1", 0, 0, "AA==")));
        Assert.Null(ReliableInbox.SeqOf(new TransferCancelMessage("t1")));
        Assert.Throws<ArgumentException>(() => new ReliableInbox("t1").Accept(new TransferCancelMessage("t1"), 0));
    }
}
