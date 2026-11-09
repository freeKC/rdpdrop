using System.Globalization;
using RdpDrop.Client.Logging;
using RdpDrop.Core.Localization;

namespace RdpDrop.Client.Localization;

/// <summary>
/// The language of the user interface: the choice of the settings ("auto" or a two
/// letter code), applied to <see cref="CultureInfo.CurrentUICulture"/>.
/// </summary>
/// <remarks>
/// "auto" follows the Windows display language of the user, read once when the client
/// starts, before any change.
/// </remarks>
internal static class UiLanguage
{
    private static readonly CultureInfo WindowsCulture = CultureInfo.CurrentUICulture;

    /// <summary>Two letter code of the language in use, one of <see cref="UiLanguages.Codes"/>.</summary>
    public static string CurrentCode { get; private set; } = UiLanguages.English;

    /// <summary>Supported language that "auto" gives on this computer.</summary>
    public static string WindowsCode => UiLanguages.Match(WindowsCulture.TwoLetterISOLanguageName);

    /// <summary>
    /// Name of each language in that language, in the order of <see cref="UiLanguages.Codes"/>.
    /// These names are never translated.
    /// </summary>
    public static IReadOnlyList<(string Code, string NativeName)> Languages { get; } =
    [
        ("en", "English"),
        ("fr", "Français"),
        ("de", "Deutsch"),
        ("es", "Español"),
        ("it", "Italiano"),
        ("pt", "Português"),
        ("nl", "Nederlands"),
    ];

    /// <summary>
    /// Sets the user interface language of this thread and of the threads started later
    /// for <paramref name="choice"/>. Texts read after this call use the new language.
    /// </summary>
    public static void Apply(string? choice)
    {
        var code = UiLanguages.Resolve(choice, WindowsCulture.TwoLetterISOLanguageName);
        var culture = CultureInfo.GetCultureInfo(code);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CurrentCode = code;
        ClientLog.Info($"User interface language: {code} (choice {UiLanguages.NormalizeChoice(choice)}, Windows display language {WindowsCulture.Name}).");
    }
}
