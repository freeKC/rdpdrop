using System.Globalization;
using UltimateRDP.Client.Resources;

namespace UltimateRDP.Client.Localization;

/// <summary>
/// A text for the user, kept as a resource key and its arguments rather than as a
/// finished string, so that it can be shown again in another language after the user
/// changes it, and written in English to the log.
/// </summary>
/// <remarks>
/// Arguments that are themselves <see cref="LocalText"/> are rendered in the same
/// language. Texts that come from elsewhere (the agent, the control, an exception) are
/// wrapped with <see cref="Raw"/> and stay as they are.
/// </remarks>
internal sealed class LocalText
{
    private readonly string? _key;
    private readonly string? _raw;
    private readonly object?[] _args;

    private LocalText(string? key, string? raw, object?[] args)
    {
        _key = key;
        _raw = raw;
        _args = args;
    }

    /// <summary>An empty text.</summary>
    public static LocalText Empty { get; } = new(null, string.Empty, []);

    /// <summary>
    /// The text of resource <paramref name="key"/>, formatted with <paramref name="args"/>.
    /// Pass the key with <c>nameof(Strings.X)</c>, so that the compiler checks it.
    /// </summary>
    public static LocalText Of(string key, params object?[] args) => new(key, null, args);

    /// <summary>
    /// Two texts shown one after the other, separated by a middle dot, such as the stage of a
    /// transfer and the path it takes.
    /// </summary>
    public static LocalText Joined(LocalText first, LocalText second) => new(null, null, [first, second]);

    /// <summary>A text that is shown as it is, in every language.</summary>
    public static LocalText Raw(string? text) => string.IsNullOrEmpty(text) ? Empty : new(null, text, []);

    /// <summary>The text in the current user interface language.</summary>
    public override string ToString() => Render(CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);

    /// <summary>The text in English, for the log.</summary>
    public string ToEnglish() => Render(CultureInfo.InvariantCulture, CultureInfo.InvariantCulture);

    private string Render(CultureInfo language, CultureInfo formatting)
    {
        if (_raw is not null)
        {
            return _raw;
        }

        if (_key is null)
        {
            return string.Join(" \u00B7 ", _args.Select(part => part is LocalText text ? text.Render(language, formatting) : Convert.ToString(part, formatting)));
        }

        var format = Strings.ResourceManager.GetString(_key!, language) ?? _key!;
        if (_args.Length == 0)
        {
            return format;
        }

        var args = new object?[_args.Length];
        for (var i = 0; i < _args.Length; i++)
        {
            args[i] = _args[i] is LocalText text ? text.Render(language, formatting) : _args[i];
        }

        return string.Format(formatting, format, args);
    }
}
