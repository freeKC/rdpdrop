using System.Text.Json;
using System.Text.Json.Serialization;

namespace RdpDrop.Core.Protocol;

/// <summary>
/// Converts messages to and from their UTF-8 JSON form.
/// </summary>
public static class MessageSerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>
    /// Serializes a message to UTF-8 JSON. The <c>type</c> field comes first, property
    /// names are camelCase and null fields are left out.
    /// </summary>
    public static byte[] Serialize(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.SerializeToUtf8Bytes(message, Options);
    }

    /// <summary>
    /// Reads a message from UTF-8 JSON. Unknown fields are ignored. Returns null when the
    /// input is not valid UTF-8 JSON, is not an object, has a missing or unknown
    /// <c>type</c>, lacks a required field or holds a value of the wrong kind.
    /// Never throws for bad input.
    /// </summary>
    public static Message? Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<Message>(utf8Json, Options);
            return message is not null && message.IsValid() ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            // Raised when the type discriminator is missing or unknown.
            return null;
        }
        catch (ArgumentException)
        {
            // Raised for some malformed UTF-8 sequences.
            return null;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            AllowOutOfOrderMetadataProperties = true,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            NumberHandling = JsonNumberHandling.Strict,
            MaxDepth = 16,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
