using System.Text.Json;
using System.Text.Json.Nodes;

namespace UltimateRDP.Core.Agent;

/// <summary>
/// The two files of the settings of the agent: the machine wide one, written by an
/// administrator in <c>%ProgramData%\UltimateRDP\agent.json</c> and only read by the agent,
/// and the one of the user in <c>%LOCALAPPDATA%\UltimateRDP\agent.json</c>, which overrides it
/// key by key. Pure functions on JSON objects; the agent reads and writes the files.
/// </summary>
public static class SettingsLayers
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Reads the content of a settings file as a JSON object. A UTF-8 byte order mark, as
    /// Notepad may write it, comments and trailing commas are accepted. Returns null when the
    /// content is not a JSON object, with the reason in <paramref name="error"/>; an empty
    /// file is an empty object.
    /// </summary>
    public static JsonObject? ParseObject(ReadOnlySpan<byte> utf8, out string? error)
    {
        if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            utf8 = utf8[3..];
        }

        error = null;
        if (utf8.Trim((ReadOnlySpan<byte>)[0x20, 0x09, 0x0D, 0x0A]).IsEmpty)
        {
            return new JsonObject(NodeOptions);
        }

        try
        {
            if (JsonNode.Parse(utf8, NodeOptions, DocumentOptions) is JsonObject value)
            {
                // The keys are read now, so that a key written twice fails here.
                _ = value.Count;
                return value;
            }

            error = "the file does not hold a JSON object";
            return null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// The settings in force: every key of <paramref name="user"/>, and every key of
    /// <paramref name="machine"/> that the user does not set. Keys are compared without case,
    /// as the settings are read. A key whose value is null in the file of the user counts as
    /// not set there. Either object may be null. The result is a new object; the arguments do
    /// not change.
    /// </summary>
    public static JsonObject Merge(JsonObject? machine, JsonObject? user)
    {
        var result = new JsonObject(NodeOptions);
        foreach (var (key, value) in Pairs(user))
        {
            if (value is not null && !result.ContainsKey(key))
            {
                result[key] = value.DeepClone();
            }
        }

        foreach (var (key, value) in Pairs(machine))
        {
            if (value is not null && !result.ContainsKey(key))
            {
                result[key] = value.DeepClone();
            }
        }

        return result;
    }

    /// <summary>
    /// The keys of <paramref name="machine"/> that are in force, because the user does not set
    /// them, in the order of the machine file. For the log.
    /// </summary>
    public static IReadOnlyList<string> MachineKeysInForce(JsonObject? machine, JsonObject? user)
    {
        var userKeys = new HashSet<string>(
            Pairs(user).Where(pair => pair.Value is not null).Select(pair => pair.Key),
            StringComparer.OrdinalIgnoreCase);
        return [.. Pairs(machine).Where(pair => pair.Value is not null && !userKeys.Contains(pair.Key)).Select(pair => pair.Key)];
    }

    /// <summary>
    /// Sets <paramref name="key"/> to <paramref name="value"/> in the file of the user, after
    /// removing any spelling of the key in another case, so that the file keeps one value per
    /// setting. The other keys of the user stay as they are, and the machine file is never
    /// written.
    /// </summary>
    public static void SetUserValue(JsonObject user, string key, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrEmpty(key);
        foreach (var existing in user.Select(pair => pair.Key).Where(name => string.Equals(name, key, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            user.Remove(existing);
        }

        user[key] = value;
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> Pairs(JsonObject? value) =>
        value ?? Enumerable.Empty<KeyValuePair<string, JsonNode?>>();
}
