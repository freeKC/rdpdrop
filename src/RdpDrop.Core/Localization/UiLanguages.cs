namespace RdpDrop.Core.Localization;

/// <summary>
/// Languages of the user interface of the client and of the agent, named by their two
/// letter ISO 639-1 code. English is the source language and the fallback.
/// </summary>
public static class UiLanguages
{
    /// <summary>Choice that follows the Windows display language.</summary>
    public const string Automatic = "auto";

    /// <summary>English, the source language and the fallback.</summary>
    public const string English = "en";

    /// <summary>Every supported language, English first.</summary>
    public static IReadOnlyList<string> Codes { get; } = ["en", "fr", "de", "es", "it", "pt", "nl"];

    /// <summary>True when <paramref name="code"/> is a supported two letter code, in any case.</summary>
    public static bool IsSupported(string? code) =>
        code is not null && Codes.Contains(code.Trim().ToLowerInvariant());

    /// <summary>
    /// Reads a language choice from the settings: <see cref="Automatic"/> or a supported
    /// code, in lower case. Anything else, including null, gives <see cref="Automatic"/>.
    /// </summary>
    public static string NormalizeChoice(string? choice)
    {
        var trimmed = choice?.Trim().ToLowerInvariant();
        return trimmed is not null && Codes.Contains(trimmed) ? trimmed : Automatic;
    }

    /// <summary>
    /// Maps the two letter code of a culture, such as "fr" for fr-CA, to a supported
    /// language. Unsupported or missing codes give <see cref="English"/>.
    /// </summary>
    public static string Match(string? twoLetterCode)
    {
        var trimmed = twoLetterCode?.Trim().ToLowerInvariant();
        return trimmed is not null && Codes.Contains(trimmed) ? trimmed : English;
    }

    /// <summary>
    /// Language to use for <paramref name="choice"/>: the choice itself when it names a
    /// supported language, otherwise the match of <paramref name="windowsTwoLetterCode"/>,
    /// the two letter code of the Windows display language.
    /// </summary>
    public static string Resolve(string? choice, string? windowsTwoLetterCode)
    {
        var normalized = NormalizeChoice(choice);
        return normalized == Automatic ? Match(windowsTwoLetterCode) : normalized;
    }
}
