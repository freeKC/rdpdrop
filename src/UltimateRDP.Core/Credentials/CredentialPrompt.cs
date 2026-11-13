namespace UltimateRDP.Core.Credentials;

/// <summary>What made the client ask for the password, or that nothing did.</summary>
public enum CredentialPromptSource
{
    /// <summary>Nothing asks: a saved generic credential is used when Windows keeps one.</summary>
    None,

    /// <summary>The command line option <c>--ask-credentials</c>.</summary>
    CommandLine,

    /// <summary>The setting <c>credentials</c> of client.json at <c>ask</c>, also the File menu entry "Always ask for the password".</summary>
    Setting,

    /// <summary>The key <c>prompt for credentials:i:1</c> of the .rdp file.</summary>
    RdpFile,
}

/// <summary>The decision of <see cref="CredentialPrompt.Decide"/>.</summary>
/// <param name="Ask">True when the control asks for the password and no saved credential is read.</param>
/// <param name="Source">The first input that asks, in the order command line, setting, .rdp file.</param>
public sealed record CredentialPromptDecision(bool Ask, CredentialPromptSource Source)
{
    /// <summary>The decision in words, for the log. Never holds a credential.</summary>
    public string Describe() => Source switch
    {
        CredentialPromptSource.CommandLine => "the password is asked: option " + CredentialPrompt.CommandLineOption,
        CredentialPromptSource.Setting => "the password is asked: setting credentials \"ask\" of client.json",
        CredentialPromptSource.RdpFile => "the password is asked: \"prompt for credentials:i:1\" of the .rdp file",
        _ => "a saved credential is used when Windows keeps one",
    };
}

/// <summary>
/// Whether the client asks for the password instead of applying the saved generic
/// credential <c>TERMSRV/&lt;host&gt;</c>, since 0.9.0. Any of three inputs asks: the option
/// <c>--ask-credentials</c>, the setting <c>credentials</c> at <c>ask</c>, and the key
/// <c>prompt for credentials:i:1</c> of the .rdp file, which 0.8.0 ignored when a saved
/// credential existed and which the Microsoft client honours.
/// </summary>
public static class CredentialPrompt
{
    /// <summary>The command line option.</summary>
    public const string CommandLineOption = "--ask-credentials";

    /// <summary>The setting value that uses a saved credential, the default.</summary>
    public const string Saved = "saved";

    /// <summary>The setting value that always asks.</summary>
    public const string AskValue = "ask";

    /// <summary>
    /// Reads the setting <c>credentials</c>: true for <c>ask</c>, false for <c>saved</c>, a
    /// missing or empty value; any other value gives false and <paramref name="recognized"/> false.
    /// </summary>
    public static bool ParseSetting(string? value, out bool recognized)
    {
        recognized = true;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or Saved:
                return false;

            case AskValue:
                return true;

            default:
                recognized = false;
                return false;
        }
    }

    /// <summary>The value the settings file writes.</summary>
    public static string ToSetting(bool ask) => ask ? AskValue : Saved;

    /// <summary>Decides from the three inputs; the first that asks is named.</summary>
    public static CredentialPromptDecision Decide(bool commandLine, bool settingAsks, bool rdpFilePrompts)
    {
        if (commandLine)
        {
            return new CredentialPromptDecision(true, CredentialPromptSource.CommandLine);
        }

        if (settingAsks)
        {
            return new CredentialPromptDecision(true, CredentialPromptSource.Setting);
        }

        return rdpFilePrompts
            ? new CredentialPromptDecision(true, CredentialPromptSource.RdpFile)
            : new CredentialPromptDecision(false, CredentialPromptSource.None);
    }
}
