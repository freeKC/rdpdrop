using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

public class ReliableOutboxTests
{
    private static QueuedLine Line(long seq, int length = 10) => new(new string('x', length - 1) + "\n", MessageTypes.FileChunk, "t1", seq * 100, seq);

    private static ReliableOutbox Filled(int count)
    {
        var outbox = new ReliableOutbox();
        for (var seq = 0; seq < count; seq++)
        {
            outbox.Add(Line(seq));
        }

        return outbox;
    }

    [Fact]
    public void Lines_are_kept_in_order_until_an_acknowledgement_covers_them()
    {
        var outbox = Filled(5);
        Assert.Equal(5, outbox.Count);
        Assert.Equal(50, outbox.Characters);

        Assert.Equal(3, outbox.Acknowledge(2));
        Assert.Equal(0, outbox.Acknowledge(1));

        Assert.Equal(3, outbox.FirstSeq);
        Assert.Equal(5, outbox.NextSeq);
        Assert.Equal(2, outbox.Count);
        Assert.Equal(20, outbox.Characters);
    }

    [Fact]
    public void A_line_must_carry_the_next_number()
    {
        var outbox = Filled(2);

        Assert.Throws<ArgumentException>(() => outbox.Add(Line(3)));
        Assert.Throws<ArgumentException>(() => outbox.Add(Line(1)));
        outbox.Add(Line(2));
    }

    [Fact]
    public void The_lines_asked_for_come_back_marked_as_sent_again()
    {
        var outbox = Filled(6);
        outbox.Acknowledge(1);

        var lines = outbox.Take(0, 3);

        Assert.Equal([2L, 3L], lines.Select(line => line.Seq));
        Assert.All(lines, line => Assert.True(line.IsResend));
        Assert.Equal(Line(2).Text, lines[0].Text);
        Assert.Equal(200, lines[0].TransferPosition);
        Assert.Equal(1, outbox.Requests);
        Assert.Equal(2, outbox.ResentLines);
        Assert.Equal(4, outbox.Count);
    }

    [Fact]
    public void Lines_never_sent_or_already_acknowledged_are_left_out()
    {
        var outbox = Filled(3);
        outbox.Acknowledge(2);

        Assert.Empty(outbox.Take(0, 2));
        Assert.Empty(outbox.Take(3, 10));
        Assert.Equal(2, outbox.Requests);
        Assert.Equal(0, outbox.ResentLines);
    }

    [Fact]
    public void The_newest_line_can_go_again()
    {
        var outbox = Filled(3);

        var newest = outbox.TakeNewest();

        Assert.Equal(2, newest?.Seq);
        Assert.True(newest?.IsResend);
        Assert.Equal(1, outbox.Probes);
        outbox.Acknowledge(2);
        Assert.Null(outbox.TakeNewest());
    }

    /// <summary>A chunk sent again for a damaged line is the very string built by TransferLines the first time.</summary>
    [Theory]
    [InlineData(ChunkEncodings.U15)]
    [InlineData(ChunkEncodings.Base64)]
    public void A_chunk_goes_again_as_the_same_string(string encoding)
    {
        var data = new byte[TransferSender.DefaultChunkSize];
        new Random(3).NextBytes(data);
        var outbox = new ReliableOutbox();
        var text = TransferLines.EncodeChunk("t1", 0, 0, data, encoding, seq: 0);
        outbox.Add(new QueuedLine(text, MessageTypes.FileChunk, "t1", 0, 0));

        var again = Assert.Single(outbox.Take(0, 0, damaged: true));

        Assert.Same(text, again.Text);
        Assert.Equal(TransferLines.EncodeChunk("t1", 0, 0, data, encoding, seq: 0), again.Text);
        Assert.True(again.IsResend);
        Assert.Equal(1, outbox.Requests);
        Assert.Equal(1, outbox.DamagedRequests);
        Assert.Empty(outbox.Take(1, 1));
        Assert.Equal(2, outbox.Requests);
        Assert.Equal(1, outbox.DamagedRequests);
    }

    [Fact]
    public void A_long_transfer_does_not_keep_what_was_acknowledged()
    {
        var outbox = new ReliableOutbox();
        for (var seq = 0; seq < 10_000; seq++)
        {
            outbox.Add(Line(seq));
            if (seq % 64 == 63)
            {
                outbox.Acknowledge(seq - 8);
            }
        }

        Assert.Equal(9_999 - 9_983 + 8, outbox.Count);
        Assert.Equal([9_990L, 9_991L], outbox.Take(9_990, 9_991).Select(line => line.Seq));
    }
}
