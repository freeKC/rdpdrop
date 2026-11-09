using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

public class TransferRegistryTests
{
    private static readonly TransferItem[] Items = [new TransferItem("a.psd", false)];

    private static FileChunkMessage Chunk(long offset, byte[] data) => new("t1", 0, offset, Convert.ToBase64String(data));

    /// <summary>
    /// The first failure seen in production: the client sent drop, transferBegin, fileBegin
    /// and the first chunks at once, and the agent had no receiver for the transfer when
    /// transferBegin arrived. The receiver then got fileBegin without transferBegin.
    /// </summary>
    [Fact]
    public void Messages_before_the_deferred_drop_wait_and_reach_the_receiver_in_order()
    {
        var registry = new TransferRegistry<TransferReceiver>();
        registry.Offer("t1");
        var data = new byte[100];
        Message[] early =
        [
            new TransferBeginMessage("t1", 1, data.Length),
            new FileBeginMessage("t1", 0, "a.psd", data.Length),
            Chunk(0, data.AsSpan(0, 60).ToArray()),
        ];

        foreach (var message in early)
        {
            Assert.Equal(TransferRoute.Queued, registry.Route(message, "t1", out var none));
            Assert.Null(none);
        }

        Assert.True(registry.IsOffered("t1"));
        Assert.Equal(3, registry.QueuedCount("t1"));

        // The deferred drop is handled now and creates the receiver.
        var storage = new MemoryTransferStorage();
        using var receiver = new TransferReceiver("t1", Items, storage, () => 0);
        var waiting = registry.Attach("t1", receiver);

        Assert.Equal(early, waiting);
        var acks = waiting.Select(receiver.Handle).ToList();
        Assert.Equal(TransferState.Receiving, receiver.State);
        Assert.Equal(TransferFailure.None, receiver.Failure);
        Assert.Equal([null, null, new FileAckMessage("t1", 0, 60)], acks);
        Assert.False(registry.IsOffered("t1"));
        Assert.Equal(0, registry.QueuedCount("t1"));

        // Later messages go straight to the receiver.
        var rest = Chunk(60, data.AsSpan(60).ToArray());
        Assert.Equal(TransferRoute.Deliver, registry.Route(rest, "t1", out var target));
        Assert.Same(receiver, target);
        receiver.Handle(rest);
        receiver.Handle(new FileEndMessage("t1", 0, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data))));
        Assert.Equal(TransferState.Succeeded, receiver.State);
        Assert.Equal(data, storage.Content("a.psd"));
    }

    [Fact]
    public void Without_an_offer_a_transfer_gets_one_answer_then_silence()
    {
        var registry = new TransferRegistry<object>();
        var begin = new TransferBeginMessage("t9", 1, 10);

        Assert.Equal(TransferRoute.Unknown, registry.Route(begin, "t9", out _));
        Assert.Equal(TransferRoute.Late, registry.Route(begin, "t9", out _));
    }

    [Fact]
    public void An_ended_transfer_drops_its_late_messages()
    {
        var registry = new TransferRegistry<object>();
        registry.Offer("t1");
        registry.Attach("t1", new object());

        registry.End("t1");

        Assert.Equal(TransferRoute.Late, registry.Route(Chunk(0, [1]), "t1", out var target));
        Assert.Null(target);
        Assert.Null(registry.Find("t1"));
    }

    [Fact]
    public void A_withdrawn_offer_says_whether_the_client_had_started()
    {
        var registry = new TransferRegistry<object>();
        registry.Offer("quiet");
        registry.Offer("started");
        registry.Route(new TransferBeginMessage("started", 1, 10), "started", out _);

        Assert.False(registry.Withdraw("quiet"));
        Assert.True(registry.Withdraw("started"));
        Assert.False(registry.IsOffered("started"));

        // Nothing waits any more: a later message is answered as unknown, once.
        Assert.Equal(TransferRoute.Unknown, registry.Route(new FileBeginMessage("quiet", 0, "a", 1), "quiet", out _));
        Assert.Equal(TransferRoute.Late, registry.Route(new FileBeginMessage("quiet", 0, "a", 1), "quiet", out _));
    }

    [Fact]
    public void A_running_transfer_is_not_withdrawn()
    {
        var registry = new TransferRegistry<object>();
        var target = new object();
        registry.Offer("t1");
        registry.Attach("t1", target);

        Assert.False(registry.Withdraw("t1"));
        Assert.Same(target, registry.Find("t1"));
    }

    [Fact]
    public void Too_much_data_before_the_receiver_ends_the_transfer()
    {
        var registry = new TransferRegistry<object>();
        registry.Offer("t1");
        var line = Chunk(0, new byte[TransferSender.MaxChunkSize]);
        var routes = new List<TransferRoute>();
        for (var i = 0; i < 512 && (routes.Count == 0 || routes[^1] == TransferRoute.Queued); i++)
        {
            routes.Add(registry.Route(line, "t1", out _));
        }

        Assert.Equal(TransferRoute.Overflow, routes[^1]);
        Assert.True(routes.Count - 1 >= 2 * TransferSender.MaxWindowBytes / TransferSender.MaxChunkSize, "A client that keeps to its window must never overflow.");
        Assert.Equal(TransferRoute.Late, registry.Route(line, "t1", out _));
    }

    [Fact]
    public void The_order_of_the_calls_is_enforced()
    {
        var registry = new TransferRegistry<object>();

        Assert.Throws<InvalidOperationException>(() => registry.Attach("t1", new object()));
        registry.Offer("t1");
        Assert.Throws<InvalidOperationException>(() => registry.Offer("t1"));
        registry.Attach("t1", new object());
        Assert.Throws<InvalidOperationException>(() => registry.Attach("t1", new object()));
        registry.End("t1");
        Assert.Throws<InvalidOperationException>(() => registry.Offer("t1"));
    }

    [Fact]
    public void Late_messages_are_counted_so_that_each_one_can_be_logged()
    {
        var registry = new TransferRegistry<object>();
        registry.End("t1");

        registry.Route(Chunk(0, [1]), "t1", out _);
        registry.Route(Chunk(1, [1]), "t1", out _);

        Assert.Equal(2, registry.LateCount("t1"));
        Assert.Equal(0, registry.LateCount("t2"));
    }

    [Fact]
    public void Only_the_latest_ended_transfers_are_remembered()
    {
        var registry = new TransferRegistry<object>();
        for (var i = 0; i <= TransferRegistry<object>.RememberedTransfers; i++)
        {
            registry.End("t" + i);
        }

        Assert.Equal(TransferRoute.Late, registry.Route(Chunk(0, [1]), "t1", out _));
        Assert.Equal(TransferRoute.Unknown, registry.Route(Chunk(0, [1]), "t0", out _));
    }
}
