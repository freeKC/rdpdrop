using System.Text.Json;

namespace UltimateRDP.Core.RemoteApp;

/// <summary>
/// The rules of the loader that do not need a window, since 0.9.3: the settings it reads before
/// anything else, and the dialogs it must never hide.
/// </summary>
public static class LoaderRules
{
    /// <summary>The class of the dialogs of Windows, such as the certificate question of the control and the message boxes of the client.</summary>
    public const string DialogClass = "#32770";

    /// <summary>The class of the credential prompt of Windows, shown by another process.</summary>
    public const string CredentialPromptClass = "Credential Dialog Xaml Host";

    /// <summary>
    /// True for a visible top level window that waits for an answer of the user: a dialog of
    /// this process, such as the certificate question of the control or a message box of the
    /// client, or the credential prompt of Windows, whatever its process.
    /// </summary>
    public static bool IsAnswerAwaited(string? className, bool ofThisProcess) =>
        (ofThisProcess && string.Equals(className, DialogClass, StringComparison.Ordinal))
        || string.Equals(className, CredentialPromptClass, StringComparison.Ordinal);

    /// <summary>
    /// Reads the two settings the client needs before it shows the loader, from the bytes of
    /// client.json, without its full reader: <c>loader</c>, <c>on</c> by default or <c>off</c>, and
    /// <c>language</c>. Comments and trailing commas are accepted, as by the full reader; a file
    /// that cannot be read gives the defaults.
    /// </summary>
    public static (bool Loader, string? Language) ReadEarlySettings(ReadOnlySpan<byte> json)
    {
        var loader = true;
        string? language = null;
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            json = json[3..];
        }

        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return (true, null);
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                if (!reader.Read())
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.String && string.Equals(name, "loader", StringComparison.OrdinalIgnoreCase))
                {
                    loader = ParseLoader(reader.GetString(), out _);
                }
                else if (reader.TokenType == JsonTokenType.String && string.Equals(name, "language", StringComparison.OrdinalIgnoreCase))
                {
                    language = reader.GetString();
                }
                else
                {
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            // The full reader says what is wrong with the file; the loader keeps its defaults.
        }

        return (loader, language);
    }

    /// <summary>Reads the setting <c>loader</c>: true for <c>on</c>, a missing or empty value; false for <c>off</c>; any other value gives true and <paramref name="recognized"/> false.</summary>
    public static bool ParseLoader(string? value, out bool recognized)
    {
        recognized = true;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "on":
                return true;

            case "off":
                return false;

            default:
                recognized = false;
                return true;
        }
    }

    /// <summary>The value the settings file writes: "on" or "off".</summary>
    public static string ToSetting(bool loader) => loader ? "on" : "off";
}
