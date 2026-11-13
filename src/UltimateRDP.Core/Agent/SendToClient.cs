using UltimateRDP.Core.Localization;

namespace UltimateRDP.Core.Agent;

/// <summary>
/// The files sent from the session to the client computer, since 0.9.0: the settings that
/// allow them on each side, and the names of the shortcut of the Send To folder of the
/// session. The names live here rather than in the resources of the agent because the agent
/// must know them all at once: at its start it removes a shortcut left in any language.
/// </summary>
public static class SendToClient
{
    /// <summary>The name of the shortcut, without its .lnk, in each language of the user interface.</summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "My PC (UltimateRDP)",
        ["fr"] = "Mon PC (UltimateRDP)",
        ["de"] = "Mein PC (UltimateRDP)",
        ["es"] = "Mi PC (UltimateRDP)",
        ["it"] = "Il mio PC (UltimateRDP)",
        ["pt"] = "Meu PC (UltimateRDP)",
        ["nl"] = "Mijn pc (UltimateRDP)",
    };

    /// <summary>Every name the shortcut may have, one per language.</summary>
    public static IReadOnlyList<string> AllShortcutNames { get; } = [.. UiLanguages.Codes.Select(code => Names[code])];

    /// <summary>The name of the shortcut, without its .lnk, for the language of the client; English for a language it does not know.</summary>
    public static string ShortcutName(string? language) =>
        language is not null && Names.TryGetValue(language, out var name) ? name : Names["en"];

    /// <summary>
    /// Reads a setting that is <c>auto</c> or <c>never</c>: <c>sendToClient</c> of agent.json and
    /// <c>receiveFiles</c> of client.json. True for auto, the default, and for a missing value;
    /// any other value gives true and <paramref name="recognized"/> false.
    /// </summary>
    public static bool ParseAutoNever(string? value, out bool recognized)
    {
        recognized = true;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "auto":
                return true;

            case "never":
                return false;

            default:
                recognized = false;
                return true;
        }
    }

    /// <summary>The value the settings file writes: "auto" or "never".</summary>
    public static string ToSetting(bool enabled) => enabled ? "auto" : "never";
}
