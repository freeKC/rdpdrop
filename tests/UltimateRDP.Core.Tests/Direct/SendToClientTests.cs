using System.Text;
using System.Text.Json;
using UltimateRDP.Core.Agent;
using UltimateRDP.Core.Localization;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Direct;

/// <summary>The messages, names and settings of the files that the session sends to the client, since 0.9.0.</summary>
public class SendToClientTests
{
    private static readonly string Hex = new('a', 64);

    private static readonly DirectTransferOffer Offer = new(3400, Hex, new string('b', 64), null, 2, 8);

    private static Message? RoundTrip(Message message) => MessageSerializer.Deserialize(MessageSerializer.Serialize(message));

    [Fact]
    public void A_send_offer_reads_back_with_its_items_and_direct_offer()
    {
        var offer = new SendOfferMessage("0f3c", 3, 123456, "report.pdf", [new TransferItem("report.pdf", false), new TransferItem("Photos", true)], Offer);
        var json = JsonDocument.Parse(MessageSerializer.Serialize(offer)).RootElement;

        Assert.Equal("sendOffer", json.GetProperty("type").GetString());
        Assert.Equal(2, json.GetProperty("direct").GetProperty("version").GetInt32());
        Assert.True(json.GetProperty("items")[1].GetProperty("isDirectory").GetBoolean());
        Assert.Equal(offer, RoundTrip(offer));
    }

    [Theory]
    [InlineData("""{"type":"sendOffer","id":"1","files":1,"bytes":5,"firstItem":"a","items":[],"direct":{"port":3400,"token":"x","certSha256":"y"}}""")]
    [InlineData("""{"type":"sendOffer","id":"1","files":1,"bytes":5,"firstItem":"a","items":[{"name":"..\\x","isDirectory":false}],"direct":{"port":3400,"token":"x","certSha256":"y"}}""")]
    [InlineData("""{"type":"sendOffer","id":"1","files":1,"bytes":5,"firstItem":"a","items":[{"name":"a","isDirectory":false},{"name":"A","isDirectory":true}],"direct":{"port":3400,"token":"x","certSha256":"y"}}""")]
    [InlineData("""{"type":"sendOffer","id":"","files":1,"bytes":5,"firstItem":"a","items":[{"name":"a","isDirectory":false}],"direct":{"port":3400,"token":"x","certSha256":"y"}}""")]
    [InlineData("""{"type":"sendOffer","id":"1","files":-1,"bytes":5,"firstItem":"a","items":[{"name":"a","isDirectory":false}],"direct":{"port":3400,"token":"x","certSha256":"y"}}""")]
    public void A_send_offer_without_items_with_a_bad_or_repeated_name_or_without_id_is_refused(string json)
    {
        Assert.Null(MessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void The_answers_read_back()
    {
        SendAckMessage[] acks = [new("0f3c", true), new("0f3c", false, "Une autre transmission est en cours.")];
        SendCancelMessage[] cancels = [new("0f3c"), new("0f3c", "The user cancelled the transfer."), new("0f3c", "no route", NotAvailable: true)];

        Assert.All(acks, ack => Assert.Equal(ack, RoundTrip(ack)));
        Assert.All(cancels, cancel => Assert.Equal(cancel, RoundTrip(cancel)));
        Assert.False(JsonDocument.Parse(MessageSerializer.Serialize(acks[0])).RootElement.TryGetProperty("message", out _));
    }

    [Fact]
    public void The_hello_of_a_client_says_that_it_accepts_files_only_when_it_does()
    {
        var accepts = JsonDocument.Parse(MessageSerializer.Serialize(HelloMessage.FromClient("0.9.0", "fr", acceptsFiles: true))).RootElement;
        var older = Assert.IsType<HelloMessage>(MessageSerializer.Deserialize("""{"type":"hello","version":1,"clientVersion":"0.8.0"}"""u8));

        Assert.True(accepts.GetProperty("acceptsFiles").GetBoolean());
        Assert.False(JsonDocument.Parse(MessageSerializer.Serialize(HelloMessage.FromClient("0.9.0"))).RootElement.TryGetProperty("acceptsFiles", out _));
        Assert.Null(older.AcceptsFiles);
    }

    [Fact]
    public void A_message_of_a_type_unknown_to_the_reader_reads_as_nothing()
    {
        // What a client 0.8.0 does with a sendOffer: its reader does not know the type, so
        // the line is logged and ignored.
        Assert.Null(MessageSerializer.Deserialize("""{"type":"sendNothing","id":"1"}"""u8));
    }

    [Theory]
    [InlineData("report.pdf", false, new string[0], "report.pdf")]
    [InlineData("report.pdf", false, new[] { "report.pdf" }, "report (2).pdf")]
    [InlineData("report.pdf", false, new[] { "report.pdf", "report (2).pdf", "report (3).pdf" }, "report (4).pdf")]
    [InlineData("archive.tar.gz", false, new[] { "archive.tar.gz" }, "archive.tar (2).gz")]
    [InlineData("Photos", true, new[] { "Photos" }, "Photos (2)")]
    [InlineData("Photos.2026", true, new[] { "Photos.2026" }, "Photos.2026 (2)")]
    [InlineData(".config", false, new[] { ".config" }, ".config (2)")]
    [InlineData("README", false, new[] { "readme" }, "README (2)")]
    public void A_name_that_exists_gets_the_next_free_number(string name, bool isDirectory, string[] taken, string expected)
    {
        var existing = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected, ReceiveNames.Unique(name, isDirectory, existing.Contains));
    }

    [Fact]
    public void Without_a_free_name_the_receive_fails_rather_than_overwrite()
    {
        Assert.Throws<IOException>(() => ReceiveNames.Unique("a.txt", false, _ => true));
    }

    [Theory]
    [InlineData("0f3c9a", ".ultimaterdp-0f3c9a.part")]
    [InlineData("", null)]
    [InlineData("..", null)]
    [InlineData("a\\b", null)]
    [InlineData("a b", null)]
    public void The_hidden_folder_of_a_transfer_comes_from_an_id_of_letters_and_digits_only(string id, string? expected)
    {
        Assert.Equal(expected, ReceiveNames.PartFolderName(id));
    }

    [Theory]
    [InlineData("en", "My PC (UltimateRDP)")]
    [InlineData("fr", "Mon PC (UltimateRDP)")]
    [InlineData("de", "Mein PC (UltimateRDP)")]
    [InlineData("es", "Mi PC (UltimateRDP)")]
    [InlineData("it", "Il mio PC (UltimateRDP)")]
    [InlineData("pt", "Meu PC (UltimateRDP)")]
    [InlineData("nl", "Mijn pc (UltimateRDP)")]
    [InlineData("ja", "My PC (UltimateRDP)")]
    [InlineData(null, "My PC (UltimateRDP)")]
    public void The_shortcut_is_named_in_the_language_of_the_client(string? language, string expected)
    {
        Assert.Equal(expected, SendToClient.ShortcutName(language));
    }

    [Fact]
    public void Every_name_of_the_shortcut_is_known_for_the_clean_up_and_is_a_valid_file_name()
    {
        Assert.Equal(UiLanguages.Codes.Count, SendToClient.AllShortcutNames.Count);
        Assert.Equal(SendToClient.AllShortcutNames.Count, SendToClient.AllShortcutNames.Distinct().Count());
        Assert.All(UiLanguages.Codes, code => Assert.Contains(SendToClient.ShortcutName(code), SendToClient.AllShortcutNames));
        Assert.All(SendToClient.AllShortcutNames, name => Assert.True(TransferPaths.IsValidName(name + ".lnk")));
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData("", true, true)]
    [InlineData(" Auto ", true, true)]
    [InlineData("NEVER", false, true)]
    [InlineData("sometimes", true, false)]
    public void The_auto_and_never_settings_read_as_the_others(string? value, bool expected, bool recognized)
    {
        Assert.Equal(expected, SendToClient.ParseAutoNever(value, out var known));
        Assert.Equal(recognized, known);
        Assert.Equal(expected ? "auto" : "never", SendToClient.ToSetting(expected));
    }
}
