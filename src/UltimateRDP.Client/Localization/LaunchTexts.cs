using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Launchers;

namespace UltimateRDP.Client.Localization;

/// <summary>The texts of the command line for the user: why it is refused, and the list of its options.</summary>
internal static class LaunchTexts
{
    /// <summary>Longest value quoted in a message, in characters.</summary>
    private const int MaxValueShown = 80;

    /// <summary>Why a command line is refused, as the end of <see cref="Strings.CommandLineError"/>.</summary>
    public static LocalText Error(LaunchError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Kind switch
        {
            LaunchErrorKind.UnknownOption => LocalText.Of(nameof(Strings.LaunchErrorUnknownOption), Shown(error.Option)),
            LaunchErrorKind.MissingValue => LocalText.Of(nameof(Strings.LaunchErrorMissingValue), error.Option),
            LaunchErrorKind.InvalidValue => LocalText.Of(nameof(Strings.LaunchErrorInvalidValue), error.Option, Shown(error.Value), Rule(error.Rule ?? LaunchValueRule.Name)),
            LaunchErrorKind.Repeated => LocalText.Of(nameof(Strings.LaunchErrorRepeated), error.Option),
            LaunchErrorKind.ShowAndHidden => LocalText.Of(nameof(Strings.LaunchErrorShowAndHidden)),
            LaunchErrorKind.TwoFiles => LocalText.Of(nameof(Strings.LaunchErrorTwoFiles)),
            LaunchErrorKind.NoServer => LocalText.Of(nameof(Strings.LaunchErrorNoServer)),
            LaunchErrorKind.NoProgram => LocalText.Of(nameof(Strings.LaunchErrorNoProgram), error.Option),
            LaunchErrorKind.Password => LocalText.Of(nameof(Strings.LaunchErrorPassword)),
            _ => LocalText.Raw(error.Message),
        };
    }

    /// <summary>What a value must be.</summary>
    public static LocalText Rule(LaunchValueRule rule) => rule switch
    {
        LaunchValueRule.Host => LocalText.Of(nameof(Strings.LaunchRuleHost)),
        LaunchValueRule.Port => LocalText.Of(nameof(Strings.LaunchRulePort)),
        LaunchValueRule.Program => LocalText.Of(nameof(Strings.LaunchRuleProgram), LaunchValues.MaxProgramLength),
        LaunchValueRule.Arguments => LocalText.Of(nameof(Strings.LaunchRuleArguments), LaunchValues.MaxArgumentsLength),
        LaunchValueRule.OnOff => LocalText.Of(nameof(Strings.LaunchRuleOnOff)),
        LaunchValueRule.Scale => LocalText.Of(nameof(Strings.LaunchRuleScale)),
        LaunchValueRule.WindowsKeys => LocalText.Of(nameof(Strings.LaunchRuleWindowsKeys)),
        LaunchValueRule.Audio => LocalText.Of(nameof(Strings.LaunchRuleAudio)),
        _ => LocalText.Of(nameof(Strings.LaunchRuleName), LaunchValues.MaxNameLength),
    };

    /// <summary>The list of the options, for <c>--help</c>.</summary>
    public static LocalText Help() => LocalText.Of(nameof(Strings.CommandLineHelp));

    /// <summary>A value in a message: control characters shown as spaces, a long one cut.</summary>
    private static string Shown(string? value)
    {
        var text = new string((value ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return text.Length > MaxValueShown ? string.Concat(text.AsSpan(0, MaxValueShown), "...") : text;
    }
}
