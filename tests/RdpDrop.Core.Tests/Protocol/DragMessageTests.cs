using System.Text;
using System.Text.Json;
using RdpDrop.Core.Protocol;

namespace RdpDrop.Core.Tests.Protocol;

public class DragMessageTests
{
    private static Message RoundTrip(Message message) =>
        MessageSerializer.Deserialize(MessageSerializer.Serialize(message))
        ?? throw new InvalidOperationException("Deserialize returned null.");

    private static JsonElement ToJson(Message message) =>
        JsonDocument.Parse(MessageSerializer.Serialize(message)).RootElement;

    private static Message? Parse(string json) => MessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

    private static readonly DropItem[] SampleItems =
    [
        new DropItem(@"C:\Users\me\a.txt", false),
        new DropItem(@"D:\Data\Folder", true),
    ];

    [Fact]
    public void Type_property_matches_discriminator()
    {
        Assert.Equal("dragEnter", new DragEnterMessage("a", 0, 0, []).Type);
        Assert.Equal("dragMove", new DragMoveMessage("a", 0, 0).Type);
        Assert.Equal("dragLeave", new DragLeaveMessage("a").Type);
        Assert.Equal("dragAck", new DragAckMessage("a", true).Type);
    }

    [Fact]
    public void Key_state_flags_match_the_spec()
    {
        Assert.Equal(0, KeyStates.None);
        Assert.Equal(1, KeyStates.Control);
        Assert.Equal(2, KeyStates.Shift);
        Assert.Equal(4, KeyStates.Alt);
        Assert.Equal(7, KeyStates.All);
    }

    [Theory]
    [InlineData(0, "none")]
    [InlineData(1, "Ctrl")]
    [InlineData(2, "Shift")]
    [InlineData(4, "Alt")]
    [InlineData(3, "Ctrl+Shift")]
    [InlineData(7, "Ctrl+Shift+Alt")]
    [InlineData(8, "none")]
    public void Key_state_is_described(int keyState, string expected)
    {
        Assert.Equal(expected, KeyStates.Describe(keyState));
    }

    [Fact]
    public void Drag_enter_round_trips()
    {
        var enter = new DragEnterMessage("id-1", 412, 300, SampleItems, KeyStates.Control | KeyStates.Shift);

        Assert.Equal(enter, RoundTrip(enter));
    }

    [Fact]
    public void Drag_enter_json_has_spec_shape()
    {
        var json = ToJson(new DragEnterMessage("id-1", 412, 300, [new DropItem(@"C:\a.txt", false)], KeyStates.Alt));

        Assert.Equal("dragEnter", json.GetProperty("type").GetString());
        Assert.Equal("id-1", json.GetProperty("id").GetString());
        Assert.Equal(412, json.GetProperty("x").GetInt32());
        Assert.Equal(300, json.GetProperty("y").GetInt32());
        Assert.Equal(4, json.GetProperty("keyState").GetInt32());
        var item = Assert.Single(json.GetProperty("items").EnumerateArray());
        Assert.Equal(@"C:\a.txt", item.GetProperty("clientPath").GetString());
    }

    [Fact]
    public void Drag_enter_without_key_state_reads_as_no_key()
    {
        const string json = """{ "type": "dragEnter", "id": "a", "x": 1, "y": 2, "items": [ { "clientPath": "C:\\a.txt", "isDirectory": false } ] }""";

        var enter = Assert.IsType<DragEnterMessage>(Parse(json));

        Assert.Equal(KeyStates.None, enter.KeyState);
        Assert.Equal(@"C:\a.txt", Assert.Single(enter.Items).ClientPath);
    }

    [Fact]
    public void Drag_enter_equality_compares_items_in_order()
    {
        var a = new DragEnterMessage("id", 1, 2, SampleItems);
        var same = new DragEnterMessage("id", 1, 2, [.. SampleItems]);
        var reordered = new DragEnterMessage("id", 1, 2, [SampleItems[1], SampleItems[0]]);

        Assert.Equal(a, same);
        Assert.Equal(a.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(a, reordered);
        Assert.NotEqual(a, a with { KeyState = 1 });
    }

    [Fact]
    public void Drag_move_round_trips()
    {
        var move = new DragMoveMessage("id-1", -20, 1080, KeyStates.Shift);

        var json = ToJson(move);

        Assert.Equal("dragMove", json.GetProperty("type").GetString());
        Assert.Equal(-20, json.GetProperty("x").GetInt32());
        Assert.Equal(2, json.GetProperty("keyState").GetInt32());
        Assert.Equal(move, RoundTrip(move));
    }

    [Fact]
    public void Drag_leave_round_trips()
    {
        var leave = new DragLeaveMessage("id-1");

        Assert.Equal("id-1", ToJson(leave).GetProperty("id").GetString());
        Assert.Equal(leave, RoundTrip(leave));
    }

    [Fact]
    public void Drag_ack_round_trips_with_and_without_message()
    {
        var accepted = new DragAckMessage("id-1", true);
        var refused = new DragAckMessage("id-2", false, "Drives are not redirected.");

        Assert.False(ToJson(accepted).TryGetProperty("message", out _));
        Assert.True(ToJson(accepted).GetProperty("accepted").GetBoolean());
        Assert.Equal(accepted, RoundTrip(accepted));
        Assert.Equal(refused, RoundTrip(refused));
    }

    [Fact]
    public void Drop_of_version_0_1_reads_with_no_key()
    {
        const string json = """{ "type": "drop", "id": "abc", "x": 4, "y": 3, "items": [] }""";

        var drop = Assert.IsType<DropMessage>(Parse(json));

        Assert.Equal(KeyStates.None, drop.KeyState);
    }

    [Fact]
    public void Drop_with_key_state_round_trips()
    {
        var drop = new DropMessage("abc", 4, 3, SampleItems, KeyStates.Control);

        Assert.Equal(1, ToJson(drop).GetProperty("keyState").GetInt32());
        Assert.Equal(drop, RoundTrip(drop));
        Assert.NotEqual(drop, drop with { KeyState = 0 });
    }

    [Theory]
    [InlineData(DropEffect.Copy, "copy")]
    [InlineData(DropEffect.Link, "link")]
    [InlineData(DropEffect.None, "none")]
    [InlineData(DropEffect.FallbackCopy, "fallbackCopy")]
    public void Drop_result_effect_round_trips(DropEffect effect, string expected)
    {
        var result = new DropResultMessage("id", DropStatus.Ok, Message: "done", Effect: effect);

        Assert.Equal(expected, ToJson(result).GetProperty("effect").GetString());
        Assert.Equal(result, RoundTrip(result));
    }

    [Fact]
    public void Drop_result_target_window_round_trips()
    {
        var result = new DropResultMessage(
            "id",
            DropStatus.Ok,
            Effect: DropEffect.Copy,
            TargetWindow: new TargetWindowInfo("CabinetWClass", "Documents", "DirectUIHWND", "explorer"));

        var json = ToJson(result);
        var window = json.GetProperty("targetWindow");

        Assert.Equal("CabinetWClass", window.GetProperty("className").GetString());
        Assert.Equal("Documents", window.GetProperty("title").GetString());
        Assert.Equal("DirectUIHWND", window.GetProperty("childClassName").GetString());
        Assert.Equal("explorer", window.GetProperty("processName").GetString());
        Assert.Equal(result, RoundTrip(result));
    }

    [Fact]
    public void Drop_result_of_version_0_1_has_no_effect()
    {
        const string json = """{ "type": "dropResult", "id": "x", "status": "ok", "targetFolder": "C:\\Users\\me\\Desktop" }""";

        var result = Assert.IsType<DropResultMessage>(Parse(json));

        Assert.Null(result.Effect);
        Assert.Null(result.TargetWindow);
    }

    [Fact]
    public void Drop_result_without_effect_writes_no_effect_field()
    {
        var json = ToJson(new DropResultMessage("x", DropStatus.Ok));

        Assert.False(json.TryGetProperty("effect", out _));
        Assert.False(json.TryGetProperty("targetWindow", out _));
    }

    [Fact]
    public void Agent_hello_with_native_drop_has_the_flag()
    {
        var json = ToJson(HelloMessage.FromAgent("0.2.0", driveRedirectionAvailable: true, nativeDrop: true));

        Assert.True(json.GetProperty("nativeDrop").GetBoolean());
        var hello = Assert.IsType<HelloMessage>(RoundTrip(HelloMessage.FromAgent("0.2.0", true, true)));
        Assert.True(hello.NativeDrop);
    }

    [Fact]
    public void Hello_of_version_0_1_has_no_native_drop()
    {
        const string json = """{ "type": "hello", "version": 1, "agentVersion": "0.1.0", "driveRedirectionAvailable": true }""";

        var hello = Assert.IsType<HelloMessage>(Parse(json));

        Assert.Null(hello.NativeDrop);
        Assert.False(ToJson(HelloMessage.FromClient("0.2.0")).TryGetProperty("nativeDrop", out _));
    }

    [Theory]
    [InlineData("""{ "type": "dragEnter", "x": 1, "y": 2, "items": [] }""")]
    [InlineData("""{ "type": "dragEnter", "id": "", "x": 1, "y": 2, "items": [] }""")]
    [InlineData("""{ "type": "dragEnter", "id": "a", "x": 1, "y": 2 }""")]
    [InlineData("""{ "type": "dragEnter", "id": "a", "x": 1, "y": 2, "items": [null] }""")]
    [InlineData("""{ "type": "dragEnter", "id": "a", "x": 1, "y": 2, "items": [{ "clientPath": "" , "isDirectory": false }] }""")]
    [InlineData("""{ "type": "dragEnter", "id": "a", "x": 1, "y": 2, "items": [], "keyState": "1" }""")]
    [InlineData("""{ "type": "dragMove", "id": "a", "y": 2 }""")]
    [InlineData("""{ "type": "dragMove", "id": "", "x": 1, "y": 2 }""")]
    [InlineData("""{ "type": "dragMove", "id": "a", "x": 1.5, "y": 2 }""")]
    [InlineData("""{ "type": "dragLeave" }""")]
    [InlineData("""{ "type": "dragLeave", "id": "" }""")]
    [InlineData("""{ "type": "dragAck", "id": "a" }""")]
    [InlineData("""{ "type": "dragAck", "id": "a", "accepted": "yes" }""")]
    [InlineData("""{ "type": "dragAck", "accepted": true }""")]
    [InlineData("""{ "type": "drop", "id": "a", "x": 1, "y": 2, "items": [], "keyState": true }""")]
    [InlineData("""{ "type": "dropResult", "id": "a", "status": "ok", "effect": "move" }""")]
    [InlineData("""{ "type": "dropResult", "id": "a", "status": "ok", "effect": 1 }""")]
    [InlineData("""{ "type": "dropResult", "id": "a", "status": "ok", "targetWindow": "explorer" }""")]
    [InlineData("""{ "type": "hello", "version": 1, "nativeDrop": "true" }""")]
    public void Malformed_drag_input_returns_null(string json)
    {
        Assert.Null(Parse(json));
    }

    [Fact]
    public void Drag_enter_survives_line_framing_with_non_ascii_paths()
    {
        var enter = new DragEnterMessage("id", 5, 6, [new DropItem(@"C:\Users\Zoé\été\café.txt", false)], KeyStates.Shift);

        var line = LineFraming.EncodeLine(enter);

        Assert.True(line.All(c => c < 0x80));
        Assert.EndsWith("\n", line);
        var parsed = MessageSerializer.Deserialize(Encoding.ASCII.GetBytes(line.TrimEnd('\n')));
        Assert.Equal(enter, parsed);
    }
}
