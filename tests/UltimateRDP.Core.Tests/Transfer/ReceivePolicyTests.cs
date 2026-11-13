using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

/// <summary>What the client accepts of the files that the session sends, since 0.9.0.</summary>
public class ReceivePolicyTests
{
    private static readonly DirectTransferOffer Direct = new(3400, new string('a', 64), new string('b', 64), null, 2, 8);

    private static SendOfferMessage Offer(long bytes, DirectTransferOffer? direct = null) =>
        new("0f3c", 1, bytes, "report.pdf", [new TransferItem("report.pdf", false)], direct ?? Direct);

    [Fact]
    public void An_offer_is_taken_when_receiving_is_on_nothing_else_runs_and_the_folder_has_room()
    {
        Assert.Equal(ReceiveRefusal.None, ReceivePolicy.Check(true, false, Offer(1000), 2000));
        Assert.Equal(ReceiveRefusal.None, ReceivePolicy.Check(true, false, Offer(1000), null));
    }

    [Fact]
    public void An_offer_is_refused_when_off_busy_of_another_version_or_too_large()
    {
        Assert.Equal(ReceiveRefusal.Off, ReceivePolicy.Check(false, false, Offer(1000), 2000));
        Assert.Equal(ReceiveRefusal.Busy, ReceivePolicy.Check(true, true, Offer(1000), 2000));
        Assert.Equal(ReceiveRefusal.NotDirect, ReceivePolicy.Check(true, false, Offer(1000, Direct with { Version = 1 }), 2000));
        Assert.Equal(ReceiveRefusal.NoSpace, ReceivePolicy.Check(true, false, Offer(1000), 1000));
    }

    [Fact]
    public void The_mark_of_the_web_says_the_Internet_zone()
    {
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", ReceivePolicy.ZoneIdentifier());
    }

    [Fact]
    public void An_item_missing_from_the_part_folder_is_skipped_and_the_names_go_on_from_the_taken_ones()
    {
        var root = Path.Combine(Path.GetTempPath(), "ultimaterdp-tests-" + Guid.NewGuid().ToString("N"));
        var part = Path.Combine(root, ".ultimaterdp-1.part");
        try
        {
            Directory.CreateDirectory(Path.Combine(part, "Empty"));
            File.WriteAllText(Path.Combine(part, "a.txt"), "new");
            File.WriteAllText(Path.Combine(root, "a.txt"), "1");
            File.WriteAllText(Path.Combine(root, "a (2).txt"), "2");
            Directory.CreateDirectory(Path.Combine(root, "Empty"));

            var moved = ReceivePolicy.MoveIn(part, root, [new TransferItem("a.txt", false), new TransferItem("gone.txt", false), new TransferItem("Empty", true)]);

            Assert.Equal([Path.Combine(root, "a (3).txt"), Path.Combine(root, "Empty (2)")], moved);
            Assert.Equal("new", File.ReadAllText(Path.Combine(root, "a (3).txt")));
            Assert.Equal("1", File.ReadAllText(Path.Combine(root, "a.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
