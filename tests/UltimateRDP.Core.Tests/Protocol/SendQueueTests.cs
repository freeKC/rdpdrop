using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Tests.Protocol;

public class SendQueueTests
{
    private static QueuedLine Line(string type, int length, string subject = "d1", long position = -1) =>
        new(new string('x', length - 1) + "\n", type, subject, position);

    [Fact]
    public void An_empty_queue_gives_no_batch()
    {
        var queue = new SendQueue();

        Assert.Null(queue.TakeBatch());
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.QueuedCharacters);
    }

    [Fact]
    public void The_lines_of_a_drop_with_transfer_leave_in_one_batch_in_their_order()
    {
        var queue = new SendQueue();
        var drop = new QueuedLine("{\"type\":\"drop\"}\n", MessageTypes.Drop, "d1");
        var begin = new QueuedLine("{\"type\":\"transferBegin\"}\n", MessageTypes.TransferBegin, "t1");
        var file = new QueuedLine("{\"type\":\"fileBegin\"}\n", MessageTypes.FileBegin, "t1");
        var chunk = Line(MessageTypes.FileChunk, 174_868, "t1", 131_072);
        queue.Enqueue(drop);
        queue.Enqueue(begin);
        queue.Enqueue(file);
        queue.Enqueue(chunk);

        var batch = queue.TakeBatch();

        Assert.NotNull(batch);
        Assert.Equal([drop, begin, file, chunk], batch.Lines);
        Assert.Equal(drop.Text + begin.Text + file.Text + chunk.Text, batch.Text);
        Assert.Equal(MessageTypes.Drop, batch.FirstType);
        Assert.Same(chunk, batch.FirstChunk);
        Assert.Null(queue.TakeBatch());
        Assert.Equal(0, queue.QueuedCharacters);
    }

    [Fact]
    public void Batches_stay_under_the_limit_and_keep_the_order()
    {
        var queue = new SendQueue(100);
        var lines = Enumerable.Range(0, 10).Select(i => Line("fileChunk", 30, "t1", i)).ToList();
        lines.ForEach(queue.Enqueue);

        var batches = new List<SendBatch>();
        while (queue.TakeBatch() is { } batch)
        {
            batches.Add(batch);
        }

        Assert.All(batches, batch => Assert.InRange(batch.Text.Length, 1, 100));
        Assert.Equal([3, 3, 3, 1], batches.Select(batch => batch.Lines.Count));
        Assert.Equal(lines, batches.SelectMany(batch => batch.Lines));
        Assert.Equal(string.Concat(lines.Select(line => line.Text)), string.Concat(batches.Select(batch => batch.Text)));
    }

    [Fact]
    public void A_line_longer_than_the_limit_goes_alone()
    {
        var queue = new SendQueue(100);
        var small = Line("drop", 20);
        var large = Line("fileChunk", 250, "t1", 0);
        var after = Line("fileEnd", 20, "t1");
        queue.Enqueue(small);
        queue.Enqueue(large);
        queue.Enqueue(after);

        Assert.Equal([small], queue.TakeBatch()!.Lines);
        Assert.Equal([large], queue.TakeBatch()!.Lines);
        Assert.Equal([after], queue.TakeBatch()!.Lines);
        Assert.Null(queue.TakeBatch());
    }

    [Fact]
    public void A_batch_of_one_line_is_that_line_without_a_copy()
    {
        var queue = new SendQueue();
        var line = Line("hello", 50);
        queue.Enqueue(line);

        Assert.Same(line.Text, queue.TakeBatch()!.Text);
    }

    [Fact]
    public void Over_the_limit_once_more_than_a_full_batch_waits()
    {
        var queue = new SendQueue(100);
        queue.Enqueue(Line("fileChunk", 60, "t1", 0));
        queue.Enqueue(Line("fileChunk", 40, "t1", 1));
        Assert.False(queue.IsOverLimit);

        queue.Enqueue(Line("fileChunk", 10, "t1", 2));

        Assert.True(queue.IsOverLimit);
        Assert.Equal(110, queue.QueuedCharacters);
        Assert.Equal(100, queue.TakeBatch()!.Text.Length);
        Assert.False(queue.IsOverLimit);
    }

    [Fact]
    public void The_default_limit_is_two_hundred_thousand_characters()
    {
        var queue = new SendQueue();
        for (var i = 0; i < 30; i++)
        {
            queue.Enqueue(Line("fileChunk", 43_717, "t1", i));
        }

        var first = queue.TakeBatch()!;

        Assert.Equal(200_000, queue.MaxBatchCharacters);
        Assert.Equal(4, first.Lines.Count);
        Assert.True(first.Text.Length <= SendQueue.DefaultMaxBatchCharacters);
    }

    /// <summary>A chunk of 128 KiB, the largest call seen to work in production, goes alone.</summary>
    [Fact]
    public void Chunks_of_128_KiB_go_one_per_call_by_default()
    {
        var queue = new SendQueue();
        queue.Enqueue(Line("fileEnd", 120, "t1"));
        queue.Enqueue(Line("fileChunk", 174_868, "t1", 0));
        queue.Enqueue(Line("fileChunk", 174_868, "t1", 1));

        Assert.Equal(2, queue.TakeBatch()!.Lines.Count);
        Assert.Single(queue.TakeBatch()!.Lines);
        Assert.Null(queue.TakeBatch());
    }

    [Theory]
    [InlineData(0, SendQueue.MinBatchLimit)]
    [InlineData(49_999, 50_000)]
    [InlineData(200_000, 200_000)]
    [InlineData(1_048_576, 1_048_576)]
    [InlineData(int.MaxValue, SendQueue.MaxBatchLimit)]
    public void A_limit_from_the_settings_is_brought_within_its_bounds(int setting, int expected)
    {
        Assert.Equal(expected, SendQueue.ClampBatchLimit(setting));
    }

    [Fact]
    public void Clear_returns_the_lines_that_waited()
    {
        var queue = new SendQueue();
        var a = Line("drop", 20);
        var b = Line("transferBegin", 20, "t1");
        queue.Enqueue(a);
        queue.Enqueue(b);

        Assert.Equal([a, b], queue.Clear());
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.QueuedCharacters);
        Assert.Null(queue.TakeBatch());
    }

    [Fact]
    public void An_empty_line_is_refused()
    {
        var queue = new SendQueue();

        Assert.Throws<ArgumentException>(() => queue.Enqueue(new QueuedLine(string.Empty, "drop", "d1")));
    }

    [Fact]
    public void A_batch_without_chunk_has_no_first_chunk()
    {
        var queue = new SendQueue();
        queue.Enqueue(Line("dragMove", 40));

        var batch = queue.TakeBatch()!;

        Assert.Null(batch.FirstChunk);
        Assert.False(batch.Lines[0].IsChunk);
    }

    /// <summary>Short lines never leave alone just before a line that does not fit with them, the case of the lost calls.</summary>
    [Fact]
    public void Short_lines_go_with_a_line_longer_than_the_limit()
    {
        var queue = new SendQueue();
        var fileEnd = Line(MessageTypes.FileEnd, 150, "t1");
        var fileBegin = Line(MessageTypes.FileBegin, 200, "t1");
        var chunk = Line(MessageTypes.FileChunk, 349_528, "t1", 0);
        var after = Line(MessageTypes.FileEnd, 150, "t1");
        queue.Enqueue(fileEnd);
        queue.Enqueue(fileBegin);
        queue.Enqueue(chunk);
        queue.Enqueue(after);

        Assert.Equal([fileEnd, fileBegin, chunk], queue.TakeBatch()!.Lines);
        Assert.Equal([after], queue.TakeBatch()!.Lines);
    }

    [Fact]
    public void Short_lines_go_with_a_line_that_does_not_fit_beside_them()
    {
        var queue = new SendQueue();
        var begin = Line(MessageTypes.TransferBegin, 120, "t1");
        var chunk = Line(MessageTypes.FileChunk, 199_950, "t1", 0);
        queue.Enqueue(begin);
        queue.Enqueue(chunk);

        var batch = queue.TakeBatch()!;

        Assert.Equal([begin, chunk], batch.Lines);
        Assert.InRange(batch.Text.Length, SendQueue.DefaultMaxBatchCharacters + 1, SendQueue.DefaultMaxBatchCharacters + SendQueue.SmallBatchCharacters);
    }

    [Fact]
    public void A_batch_of_a_thousand_characters_or_more_stays_within_the_limit()
    {
        var queue = new SendQueue();
        var lines = Line(MessageTypes.FileChunk, 1_000, "t1", 0);
        var chunk = Line(MessageTypes.FileChunk, 199_950, "t1", 1);
        queue.Enqueue(lines);
        queue.Enqueue(chunk);

        Assert.Equal([lines], queue.TakeBatch()!.Lines);
        Assert.Equal([chunk], queue.TakeBatch()!.Lines);
    }

    [Fact]
    public void A_chunk_sent_again_does_not_count_as_a_new_chunk()
    {
        var queue = new SendQueue();
        var again = Line(MessageTypes.FileChunk, 100, "t1", 0) with { IsResend = true };
        var fresh = Line(MessageTypes.FileChunk, 100, "t1", 1);
        queue.Enqueue(again);
        queue.Enqueue(fresh);

        var batch = queue.TakeBatch()!;

        Assert.Same(again, batch.FirstChunk);
        Assert.Same(fresh, batch.FirstNewChunk);
    }
}
