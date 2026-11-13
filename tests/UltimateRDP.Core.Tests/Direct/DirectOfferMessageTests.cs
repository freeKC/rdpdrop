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

    private static DirectTransferOffer Offer(int port = 3400, string? host = null, int? version = 2, int? connections = 8) =>
        new(port, Hex, Hash, host, version, connections);

    [Fact]
    public void A_dragAck_carries_the_direct_offer_under_direct_with_its_version_and_connections()
    {
        var ack = new DragAckMessage("d1", true, Transfer: true, TransferId: "t1", Direct: Offer(3401, "192.0.2.10"));
        var json = JsonDocument.Parse(MessageSerializer.Serialize(ack)).RootElement;

        var direct = json.GetProperty("direct");
        Assert.Equal(3401, direct.GetProperty("port").GetInt32());
        Assert.Equal(Hex, direct.GetProperty("token").GetString());
        Assert.Equal(Hash, direct.GetProperty("certSha256").GetString());
        Assert.Equal("192.0.2.10", direct.GetProperty("host").GetString());
        Assert.Equal(2, direct.GetProperty("version").GetInt32());
        Assert.Equal(8, direct.GetProperty("connections").GetInt32());
        Assert.Equal(ack, MessageSerializer.Deserialize(MessageSerializer.Serialize(ack)));
    }

    [Fact]
    public void A_dragAck_without_offer_or_host_writes_no_field_for_them()
    {
        var plain = JsonDocument.Parse(MessageSerializer.Serialize(new DragAckMessage("d1", true, Transfer: true, TransferId: "t1"))).RootElement;
        var noHost = JsonDocument.Parse(MessageSerializer.Serialize(new DragAckMessage("d1", true, Transfer: true, TransferId: "t1", Direct: Offer()))).RootElement;

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
    public void The_offer_of_an_agent_0_6_has_no_version_and_is_not_used()
    {
        var ack = Assert.IsType<DragAckMessage>(Parse($$$"""{"type":"dragAck","id":"d1","accepted":true,"transfer":true,"transferId":"t1","direct":{"port":3400,"token":"{{{Hex}}}","certSha256":"{{{Hash}}}"}}"""));

        Assert.NotNull(ack.Direct);
        Assert.Null(ack.Direct.Version);
        Assert.False(ack.Direct.IsWellFormed());
    }

    [Fact]
    public void A_malformed_offer_keeps_the_dragAck_and_is_not_well_formed()
    {
        var ack = Assert.IsType<DragAckMessage>(Parse($$$"""{"type":"dragAck","id":"d1","accepted":true,"transfer":true,"transferId":"t1","direct":{"port":70000,"token":"{{{Hex}}}","certSha256":"{{{Hash}}}","version":2,"connections":8,"later":1}}"""));

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
        var offer = new DirectTransferOffer(port, goodToken ? Hex : "AA" + Hex[2..], goodHash ? Hash : Hash[..62], host, 2, 8);

        Assert.Equal(expected, offer.IsWellFormed());
    }

    [Theory]
    [InlineData(2, 1, true)]
    [InlineData(2, 16, true)]
    [InlineData(2, 0, false)]
    [InlineData(2, 17, false)]
    [InlineData(2, null, false)]
    [InlineData(1, 8, false)]
    [InlineData(3, 8, false)]
    [InlineData(null, 8, false)]
    public void An_offer_is_used_only_in_version_2_with_1_to_16_connections(int? version, int? connections, bool expected)
    {
        Assert.Equal(expected, Offer(version: version, connections: connections).IsWellFormed());
    }

    [Fact]
    public void A_held_drop_keeps_a_well_formed_offer_of_the_final_answer()
    {
        var offer = Offer();
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
