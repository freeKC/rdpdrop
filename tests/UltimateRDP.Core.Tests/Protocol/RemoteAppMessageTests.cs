using System.Text;
using System.Text.Json;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Tests.Protocol;

public class RemoteAppMessageTests
{
    private static Message RoundTrip(Message message) =>
        MessageSerializer.Deserialize(MessageSerializer.Serialize(message))
        ?? throw new InvalidOperationException("Deserialize returned null.");

    private static JsonElement ToJson(Message message) =>
        JsonDocument.Parse(MessageSerializer.Serialize(message)).RootElement;

    private static Message? Parse(string json) => MessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void The_hello_of_a_remoteapp_client_says_so()
    {
        var hello = HelloMessage.FromClient("0.8.0", "fr", remoteApp: true);

        Assert.True(ToJson(hello).GetProperty("remoteApp").GetBoolean());
        Assert.Equal(hello, RoundTrip(hello));
    }

    [Fact]
    public void The_hello_of_a_desktop_client_leaves_the_field_out()
    {
        Assert.False(ToJson(HelloMessage.FromClient("0.8.0", "fr")).TryGetProperty("remoteApp", out _));
        Assert.False(ToJson(HelloMessage.FromClient("0.8.0", "fr", remoteApp: false)).TryGetProperty("remoteApp", out _));
    }

    [Fact]
    public void A_hello_of_an_older_client_reads_as_a_desktop()
    {
        var hello = Assert.IsType<HelloMessage>(Parse("""{ "type": "hello", "version": 1, "clientVersion": "0.7.0" }"""));

        Assert.Null(hello.RemoteApp);
    }

    [Fact]
    public void Unknown_fields_next_to_it_are_skipped()
    {
        // An agent 0.7.0 skips the field the same way, as an unknown one.
        var hello = Assert.IsType<HelloMessage>(Parse("""{ "type": "hello", "version": 1, "clientVersion": "0.9.0", "remoteApp": true, "later": 3 }"""));

        Assert.True(hello.RemoteApp);
    }

    [Fact]
    public void The_rectangle_of_the_target_round_trips()
    {
        var result = new DropResultMessage(
            "id-1",
            DropStatus.Ok,
            "Dropped on Inbox",
            DropEffect.Copy,
            new TargetWindowInfo("MyAppMain", "Inbox", "Edit", "myapp", new WindowRect(-1200, 40, -200, 840)));

        var json = ToJson(result).GetProperty("targetWindow").GetProperty("rect");

        Assert.Equal(-1200, json.GetProperty("left").GetInt32());
        Assert.Equal(40, json.GetProperty("top").GetInt32());
        Assert.Equal(-200, json.GetProperty("right").GetInt32());
        Assert.Equal(840, json.GetProperty("bottom").GetInt32());
        Assert.Equal(result, RoundTrip(result));
    }

    [Fact]
    public void A_target_without_a_rectangle_leaves_it_out()
    {
        var result = new DropResultMessage("id-1", DropStatus.Ok, Effect: DropEffect.Copy, TargetWindow: new TargetWindowInfo("CabinetWClass", "Documents"));

        Assert.False(ToJson(result).GetProperty("targetWindow").TryGetProperty("rect", out _));
    }

    [Fact]
    public void A_result_of_an_older_agent_has_no_rectangle()
    {
        var result = Assert.IsType<DropResultMessage>(Parse("""{ "type": "dropResult", "id": "a", "status": "ok", "effect": "copy", "targetWindow": { "className": "CabinetWClass", "title": "Documents" } }"""));

        Assert.Null(result.TargetWindow!.Rect);
    }

    [Fact]
    public void The_rectangle_reads_as_its_edges()
    {
        Assert.Equal("(-1200, 40) to (-200, 840)", new WindowRect(-1200, 40, -200, 840).ToString());
    }
}
