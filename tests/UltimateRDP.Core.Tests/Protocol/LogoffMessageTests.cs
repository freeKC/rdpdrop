using System.Text;
using System.Text.Json;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Tests.Protocol;

public class LogoffMessageTests
{
    private static Message RoundTrip(Message message) =>
        MessageSerializer.Deserialize(MessageSerializer.Serialize(message))
        ?? throw new InvalidOperationException("Deserialize returned null.");

    private static JsonElement ToJson(Message message) =>
        JsonDocument.Parse(MessageSerializer.Serialize(message)).RootElement;

    private static Message? Parse(string json) => MessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Type_property_matches_discriminator()
    {
        Assert.Equal("logoff", new LogoffMessage("a").Type);
        Assert.Equal("logoffResult", new LogoffResultMessage("a", true).Type);
    }

    [Fact]
    public void Logoff_round_trips()
    {
        var logoff = new LogoffMessage("6f1c2a52-6a0e-4a43-9d64-0c2f7a6a1b11");

        Assert.Equal(logoff, RoundTrip(logoff));
    }

    [Fact]
    public void Logoff_json_has_spec_shape()
    {
        var json = ToJson(new LogoffMessage("id-1"));

        Assert.Equal("logoff", json.GetProperty("type").GetString());
        Assert.Equal("id-1", json.GetProperty("id").GetString());
        Assert.Equal(2, json.EnumerateObject().Count());
    }

    [Fact]
    public void Logoff_reads_spec_example()
    {
        const string json = """{ "type": "logoff", "id": "abc" }""";

        Assert.Equal(new LogoffMessage("abc"), Parse(json));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Logoff_result_round_trips(bool accepted)
    {
        var result = new LogoffResultMessage("id-2", accepted, "Le texte de l'agent");

        Assert.Equal(result, RoundTrip(result));
    }

    [Fact]
    public void Logoff_result_json_has_spec_shape()
    {
        var json = ToJson(new LogoffResultMessage("id-2", false, "Access is denied."));

        Assert.Equal("logoffResult", json.GetProperty("type").GetString());
        Assert.Equal("id-2", json.GetProperty("id").GetString());
        Assert.False(json.GetProperty("accepted").GetBoolean());
        Assert.Equal("Access is denied.", json.GetProperty("message").GetString());
    }

    [Fact]
    public void Logoff_result_without_message_leaves_the_field_out()
    {
        var result = new LogoffResultMessage("id", true);

        var json = ToJson(result);

        Assert.False(json.TryGetProperty("message", out _));
        Assert.Equal(result, RoundTrip(result));
    }

    [Fact]
    public void Logoff_result_reads_spec_example()
    {
        const string json = """{ "type": "logoffResult", "id": "abc", "accepted": true, "message": "Logging off." }""";

        Assert.Equal(new LogoffResultMessage("abc", true, "Logging off."), Parse(json));
    }

    [Fact]
    public void Non_ascii_message_survives_the_line_encoding()
    {
        var result = new LogoffResultMessage("id", true, "La session distante se ferme. Où êtes-vous ?");

        var line = LineFraming.EncodeLine(result);

        Assert.True(line.All(c => c < 128));
        Assert.Equal(result, MessageSerializer.Deserialize(Encoding.ASCII.GetBytes(line.TrimEnd('\n'))));
    }

    [Theory]
    [InlineData("""{ "type": "logoff" }""")]
    [InlineData("""{ "type": "logoff", "id": "" }""")]
    [InlineData("""{ "type": "logoff", "id": null }""")]
    [InlineData("""{ "type": "logoff", "id": 5 }""")]
    [InlineData("""{ "type": "logoffResult", "id": "a" }""")]
    [InlineData("""{ "type": "logoffResult", "accepted": true }""")]
    [InlineData("""{ "type": "logoffResult", "id": "", "accepted": true }""")]
    [InlineData("""{ "type": "logoffResult", "id": "a", "accepted": "yes" }""")]
    [InlineData("""{ "type": "logoffResult", "id": "a", "accepted": 1 }""")]
    [InlineData("""{ "type": "logoffResult", "id": "a", "accepted": true, "message": 3 }""")]
    [InlineData("""{ "type": "LogOff", "id": "a" }""")]
    public void Malformed_logoff_messages_are_rejected(string json)
    {
        Assert.Null(Parse(json));
    }

    [Fact]
    public void Client_hello_carries_the_language()
    {
        var hello = HelloMessage.FromClient("0.3.0", "fr");

        var json = ToJson(hello);

        Assert.Equal("0.3.0", json.GetProperty("clientVersion").GetString());
        Assert.Equal("fr", json.GetProperty("clientLanguage").GetString());
        Assert.Equal(hello, RoundTrip(hello));
    }

    [Fact]
    public void Client_hello_without_language_leaves_the_field_out()
    {
        var json = ToJson(HelloMessage.FromClient("0.3.0"));

        Assert.False(json.TryGetProperty("clientLanguage", out _));
    }

    [Fact]
    public void Agent_hello_has_no_client_language()
    {
        var json = ToJson(HelloMessage.FromAgent("0.3.0", driveRedirectionAvailable: true));

        Assert.False(json.TryGetProperty("clientLanguage", out _));
    }

    [Fact]
    public void Client_hello_reads_spec_example()
    {
        const string json = """{ "type": "hello", "version": 1, "clientVersion": "0.3.0", "clientLanguage": "de" }""";

        var hello = Assert.IsType<HelloMessage>(Parse(json));

        Assert.Equal("de", hello.ClientLanguage);
        Assert.Equal("0.3.0", hello.ClientVersion);
    }

    [Fact]
    public void Client_hello_with_a_language_of_the_wrong_kind_is_rejected()
    {
        Assert.Null(Parse("""{ "type": "hello", "version": 1, "clientLanguage": 12 }"""));
    }
}
