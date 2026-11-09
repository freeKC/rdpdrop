using System.Globalization;
using RdpDrop.Core.Localization;

namespace RdpDrop.Agent.Localization;

/// <summary>
/// Language of the client connected to this session, given by the <c>clientLanguage</c>
/// field of its hello. The texts the agent sends follow it. English until a client says
/// otherwise, and for a client that does not say. Thread safe.
/// </summary>
internal sealed class ClientLanguage
{
    private volatile CultureInfo _culture = CultureInfo.GetCultureInfo(UiLanguages.English);

    /// <summary>Culture of the texts sent to the client.</summary>
    public CultureInfo Culture => _culture;

    /// <summary>
    /// Takes the two letter code of a hello. An unsupported or missing code gives English.
    /// Returns the code in use.
    /// </summary>
    public string Set(string? clientLanguage)
    {
        var code = UiLanguages.Match(clientLanguage);
        _culture = CultureInfo.GetCultureInfo(code);
        return code;
    }
}
