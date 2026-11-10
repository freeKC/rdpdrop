using System.Text;
using System.Text.Json;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Direct;

public class DirectOfferMessageTests
{
    private static readonly string Hex = new('a', 64);
    private static readonly string Hash = new string('0', 63) + "f";

    private static Message? Parse(string json) => MessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void A_dragAck_carries_the_direct_offer_under_direct()
    {
        var ack = new DragAckMessage("d1", true, Transfer: true, TransferId: "t1", Direct: new DirectTransferOffer(3401, Hex, Hash, "192.0.2.10"));
        var json = JsonDocument.Parse(MessageSerializer.Serialize(ack)).RootElement;

        var direct = json.GetProperty("direct");
        Assert.Equal(3401, direct.GetProperty("port").GetInt32());
        Assert.Equal(Hex, direct.GetProperty("token").GetString());
        Assert.Equal(Hash, direct.GetProperty("certSha256").GetString());
        Assert.Equal("192.0.2.10", direct.GetProperty("host").GetString());
        Assert.Equal(ack, MessageSerializer.Deserialize(MessageSerializer.Serialize(ack)));
    }

    [Fact]
    public void A_dragAck_without_offer_or_host_writes_no_field_for_them()
    {
        var plain = JsonDocument.Parse(MessageSerializer.Serialize(new DragAckMessage("d1", true, Transfer: true, TransferId: "t1"))).RootElement;
        var noHost = JsonDocument.Parse(MessageSerializer.Serialize(new DragAckMessage("d1", true, Transfer: true, TransferId: "t1", Direct: new DirectTransferOffer(3400, Hex, Hash)))).RootElement;

        Assert.False(plain.TryGetProperty("direct", out _));
        Assert.False(noHost.GetProperty("direct").TryGetProperty("host", out _));
    }

    [Fact]
    public void A_dragAck_of_an_agent_before_0_6_reads_without_offer()
    {
        var ack = Assert.IsType<DragAckMessage>(Parse("""{"type":"dragAck","id":"d1","accepted":true,"transfer":true,"transferId":"t1"}"""));

        Assert.Null(ack.Direct);
    }

    [Fact]
    public void A_malformed_offer_keeps_the_dragAck_and_is_not_well_formed()
    {
        var ack = Assert.IsType<DragAckMessage>(Parse($$$"""{"type":"dragAck","id":"d1","accepted":true,"transfer":true,"transferId":"t1","direct":{"port":70000,"token":"{{{Hex}}}","certSha256":"{{{Hash}}}","later":1}}"""));

        Assert.NotNull(ack.Direct);
        Assert.False(ack.Direct.IsWellFormed());
    }

    [Theory]
    [InlineData(3400, true, true, null, true)]
    [InlineData(0, true, true, null, false)]
    [InlineData(65536, true, true, null, false)]
    [InlineData(3400, false, true, null, false)]
    [InlineData(3400, true, false, null, false)]
    [InlineData(3400, true, true, "", false)]
    [InlineData(3400, true, true, "10.0.0.1", true)]
    public void An_offer_is_well_formed_with_a_port_and_two_hexadecimal_values(int port, bool goodToken, bool goodHash, string? host, bool expected)
    {
        var offer = new DirectTransferOffer(port, goodToken ? Hex : "AA" + Hex[2..], goodHash ? Hash : Hash[..62], host);

        Assert.Equal(expected, offer.IsWellFormed());
    }

    [Fact]
    public void A_held_drop_keeps_a_well_formed_offer_of_the_final_answer()
    {
        var offer = new DirectTransferOffer(3400, Hex, Hash);
        var decision = new DropDecision("d1", 0);
        Assert.True(decision.OnAck(new DragAckMessage("d1", true, Transfer: true, TransferId: "t1", Direct: offer), 10));
        Assert.Equal(offer, decision.Direct);
        Assert.Contains("direct connection on port 3400", decision.Describe(), StringComparison.Ordinal);

        var malformed = new DropDecision("d2", 0);
        Assert.True(malformed.OnAck(new DragAckMessage("d2", true, Transfer: true, TransferId: "t2", Direct: offer with { Port = 0 }), 10));
        Assert.Null(malformed.Direct);
        Assert.Equal(DropDecisionState.Transfer, malformed.State);
    }
}
