using System.Text.Json;

namespace UltimateRDP.Agent.Channel;

/// <summary>
/// The fields of a message that can be read without knowing its type: the protocol
/// version, the type and the id. Used to answer messages from a newer protocol
/// version, which the typed reader would not accept.
/// </summary>
internal sealed record MessageEnvelope(string? Type, int? Version, string? Id)
{
    /// <summary>Reads the envelope, or returns null when the payload is not a JSON object.</summary>
    public static MessageEnvelope? TryRead(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new MessageEnvelope(
                GetString(root, "type"),
                root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) ? number : null,
                GetString(root, "id"));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // Raised for some malformed UTF-8 sequences.
            return null;
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
