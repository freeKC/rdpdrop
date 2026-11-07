using System.Text;
using System.Text.Json;
using RdpDrop.Core.Protocol;

namespace RdpDrop.Core.Tests.Protocol;

public class MessageSerializerTests
{
    private static Message RoundTrip(Message message) =>
        MessageSerializer.Deserialize(MessageSerializer.Serialize(message))
        ?? throw new InvalidOperationException("Deserialize returned null.");

    private static JsonElement ToJson(Message message) =>
        JsonDocument.Parse(MessageSerializer.Serialize(message)).RootElement;

    private static Message? Parse(string json) => MessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Constants_match_the_spec()
    {
        Assert.Equal(1, ProtocolConstants.ProtocolVersion);
        Assert.Equal("RDPDROP", ProtocolConstants.ChannelName);
        Assert.True(ProtocolConstants.ChannelName.Length <= 7);
    }

    [Fact]
    public void Type_property_matches_discriminator()
    {
        Assert.Equal("hello", new HelloMessage(1).Type);
        Assert.Equal("drop", new DropMessage("a", 0, 0, []).Type);
        Assert.Equal("dropResult", new DropResultMessage("a", DropStatus.Ok).Type);
    }

    [Fact]
    public void Client_hello_round_trips()
    {
        var hello = HelloMessage.FromClient("0.1.0");

        var result = RoundTrip(hello);

        Assert.Equal(hello, result);
        Assert.Equal(new HelloMessage(1, ClientVersion: "0.1.0"), result);
    }

    [Fact]
    public void Agent_hello_round_trips()
    {
        var hello = HelloMessage.FromAgent("0.2.0", driveRedirectionAvailable: true);

        var result = Assert.IsType<HelloMessage>(RoundTrip(hello));

        Assert.Equal(1, result.Version);
        Assert.Null(result.ClientVersion);
        Assert.Equal("0.2.0", result.AgentVersion);
        Assert.True(result.DriveRedirectionAvailable);
    }

    [Fact]
    public void Client_hello_json_has_spec_shape()
    {
        var json = ToJson(HelloMessage.FromClient("0.1.0"));

        Assert.Equal("hello", json.GetProperty("type").GetString());
        Assert.Equal(1, json.GetProperty("version").GetInt32());
        Assert.Equal("0.1.0", json.GetProperty("clientVersion").GetString());
        Assert.False(json.TryGetProperty("agentVersion", out _));
        Assert.False(json.TryGetProperty("driveRedirectionAvailable", out _));
    }

    [Fact]
    public void Agent_hello_json_has_spec_shape()
    {
        var json = ToJson(HelloMessage.FromAgent("0.1.0", driveRedirectionAvailable: false));

        Assert.Equal("hello", json.GetProperty("type").GetString());
        Assert.Equal(1, json.GetProperty("version").GetInt32());
        Assert.Equal("0.1.0", json.GetProperty("agentVersion").GetString());
        Assert.False(json.GetProperty("driveRedirectionAvailable").GetBoolean());
        Assert.False(json.TryGetProperty("clientVersion", out _));
    }

    [Fact]
    public void Type_field_is_written_first()
    {
        var text = Encoding.UTF8.GetString(MessageSerializer.Serialize(new DropResultMessage("x", DropStatus.Ok)));

        Assert.StartsWith("{\"type\":\"dropResult\"", text);
    }

    [Fact]
    public void Drop_round_trips()
    {
        var drop = new DropMessage(
            "6f1c2a52-6a0e-4a43-9d64-0c2f7a6a1b11",
            412,
            300,
            [new DropItem(@"C:\Users\me\a.txt", false), new DropItem(@"D:\Data\Folder", true)]);

        var result = RoundTrip(drop);

        Assert.Equal(drop, result);
    }

    [Fact]
    public void Drop_json_has_spec_shape()
    {
        var json = ToJson(new DropMessage("id-1", 412, 300, [new DropItem(@"C:\Users\me\a.txt", false)]));

        Assert.Equal("drop", json.GetProperty("type").GetString());
        Assert.Equal("id-1", json.GetProperty("id").GetString());
        Assert.Equal(412, json.GetProperty("x").GetInt32());
        Assert.Equal(300, json.GetProperty("y").GetInt32());
        var item = Assert.Single(json.GetProperty("items").EnumerateArray());
        Assert.Equal(@"C:\Users\me\a.txt", item.GetProperty("clientPath").GetString());
        Assert.False(item.GetProperty("isDirectory").GetBoolean());
    }

    [Fact]
    public void Drop_reads_spec_example()
    {
        const string json = """
            { "type": "drop", "id": "abc", "x": 412, "y": 300,
              "items": [ { "clientPath": "C:\\Users\\me\\a.txt", "isDirectory": false } ] }
            """;

        var drop = Assert.IsType<DropMessage>(Parse(json));

        Assert.Equal(new DropMessage("abc", 412, 300, [new DropItem(@"C:\Users\me\a.txt", false)]), drop);
    }

    [Fact]
    public void Drop_with_no_items_round_trips()
    {
        var drop = new DropMessage("id", -5, 0, []);

        Assert.Equal(drop, RoundTrip(drop));
    }

    [Fact]
    public void Drop_equality_compares_items_in_order()
    {
        var a = new DropMessage("id", 1, 2, [new DropItem("C:\\a", false), new DropItem("C:\\b", true)]);
        var same = new DropMessage("id", 1, 2, [new DropItem("C:\\a", false), new DropItem("C:\\b", true)]);
        var reordered = new DropMessage("id", 1, 2, [new DropItem("C:\\b", true), new DropItem("C:\\a", false)]);

        Assert.Equal(a, same);
        Assert.Equal(a.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(a, reordered);
        Assert.NotEqual(a, a with { X = 9 });
    }

    [Theory]
    [InlineData(DropStatus.Ok, "ok")]
    [InlineData(DropStatus.Error, "error")]
    [InlineData(DropStatus.Cancelled, "cancelled")]
    public void Drop_result_round_trips_with_lowercase_status(DropStatus status, string expected)
    {
        var result = new DropResultMessage("id-2", status, "Dropped on Documents");

        var json = ToJson(result);

        Assert.Equal("dropResult", json.GetProperty("type").GetString());
        Assert.Equal(expected, json.GetProperty("status").GetString());
        Assert.Equal("Dropped on Documents", json.GetProperty("message").GetString());
        Assert.Equal(result, RoundTrip(result));
    }

    [Fact]
    public void Drop_result_without_optional_fields_round_trips()
    {
        var result = new DropResultMessage("id", DropStatus.Error, Message: "Drives are not redirected.");

        var json = ToJson(result);

        Assert.False(json.TryGetProperty("effect", out _));
        Assert.False(json.TryGetProperty("targetWindow", out _));
        Assert.Equal(result, RoundTrip(result));
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        const string json = """{ "type": "hello", "version": 1, "clientVersion": "0.1.0", "future": { "a": [1, 2] }, "other": null }""";

        Assert.Equal(new HelloMessage(1, ClientVersion: "0.1.0"), Parse(json));
    }

    [Fact]
    public void Type_field_may_come_after_other_fields()
    {
        const string json = """{ "id": "x", "status": "cancelled", "type": "dropResult" }""";

        Assert.Equal(new DropResultMessage("x", DropStatus.Cancelled), Parse(json));
    }

    [Fact]
    public void Higher_version_is_still_read()
    {
        var hello = Assert.IsType<HelloMessage>(Parse("""{ "type": "hello", "version": 2 }"""));

        Assert.Equal(2, hello.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"hello\"")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{ "version": 1 }""")]
    [InlineData("""{ "type": "unknown", "version": 1 }""")]
    [InlineData("""{ "type": "Hello", "version": 1 }""")]
    [InlineData("""{ "type": 5, "version": 1 }""")]
    [InlineData("""{ "type": "hello" }""")]
    [InlineData("""{ "type": "hello", "version": "1" }""")]
    [InlineData("""{ "type": "hello", "version": 1.5 }""")]
    [InlineData("""{ "type": "hello", "version": 1, "driveRedirectionAvailable": "yes" }""")]
    [InlineData("""{ "type": "drop", "x": 1, "y": 2, "items": [] }""")]
    [InlineData("""{ "type": "drop", "id": "", "x": 1, "y": 2, "items": [] }""")]
    [InlineData("""{ "type": "drop", "id": null, "x": 1, "y": 2, "items": [] }""")]
    [InlineData("""{ "type": "drop", "id": "a", "y": 2, "items": [] }""")]
    [InlineData("""{ "type": "drop", "id": "a", "x": 1, "y": 2 }""")]
    [InlineData("""{ "type": "drop", "id": "a", "x": 1, "y": 2, "items": null }""")]
    [InlineData("""{ "type": "drop", "id": "a", "x": 1, "y": 2, "items": [null] }""")]
    [InlineData("""{ "type": "drop", "id": "a", "x": 1, "y": 2, "items": [{ "isDirectory": true }] }""")]
    [InlineData("""{ "type": "drop", "id": "a", "x": 1, "y": 2, "items": [{ "clientPath": "", "isDirectory": true }] }""")]
    [InlineData("""{ "type": "drop", "id": "a", "x": 1, "y": 2, "items": {} }""")]
    [InlineData("""{ "type": "drop", "id": "a", "x": 99999999999, "y": 2, "items": [] }""")]
    [InlineData("""{ "type": "dropResult", "id": "a" }""")]
    [InlineData("""{ "type": "dropResult", "id": "a", "status": "done" }""")]
    [InlineData("""{ "type": "dropResult", "id": "a", "status": 0 }""")]
    [InlineData("""{ "type": "dropResult", "status": "ok" }""")]
    [InlineData("""{ "type": "hello", "version": 1 } trailing""")]
    public void Malformed_input_returns_null(string json)
    {
        Assert.Null(Parse(json));
    }

    [Fact]
    public void Invalid_utf8_returns_null()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes("{ \"type\": \"hello\", \"version\": 1, \"clientVersion\": \""), 0xC3, 0x28, .. Encoding.UTF8.GetBytes("\" }")];

        Assert.Null(MessageSerializer.Deserialize(bytes));
    }

    [Fact]
    public void Deeply_nested_input_returns_null()
    {
        var json = "{ \"type\": \"hello\", \"version\": 1, \"extra\": " + new string('[', 10_000) + new string(']', 10_000) + " }";

        Assert.Null(Parse(json));
    }

    [Fact]
    public void Random_bytes_never_throw()
    {
        var random = new Random(1234);
        var buffer = new byte[64];
        for (var i = 0; i < 2_000; i++)
        {
            random.NextBytes(buffer);
            _ = MessageSerializer.Deserialize(buffer.AsSpan(0, random.Next(buffer.Length)));
        }
    }

    [Fact]
    public void Truncated_messages_return_null()
    {
        var bytes = MessageSerializer.Serialize(new DropMessage("id", 1, 2, [new DropItem(@"C:\a.txt", false)]));
        for (var length = 0; length < bytes.Length; length++)
        {
            Assert.Null(MessageSerializer.Deserialize(bytes.AsSpan(0, length)));
        }
    }

    [Fact]
    public void Serialize_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => MessageSerializer.Serialize(null!));
    }

    [Fact]
    public void Non_ascii_paths_round_trip()
    {
        var drop = new DropMessage("id", 1, 2, [new DropItem(@"C:\Users\Zoé\Documents\été 2026\café.txt", false)]);

        Assert.Equal(drop, RoundTrip(drop));
    }
}
