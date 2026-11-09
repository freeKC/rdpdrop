using System.Globalization;
using RdpDrop.Agent.Resources;

namespace RdpDrop.Agent.Localization;

/// <summary>
/// A text for the user, kept as a resource key and its arguments, so that the same text
/// can be written in English to the log, in the language of the client in the messages
/// of the channel, and in the Windows display language in the tray.
/// </summary>
/// <remarks>
/// <see cref="ToString"/> gives English, so that a text written to the log stays English
/// whatever the language of the client. Arguments that are themselves
/// <see cref="LocalText"/> are rendered in the same language. Texts that come from
/// elsewhere (an exception, a window title) are plain arguments and stay as they are.
/// </remarks>
internal sealed class LocalText
{
    private readonly string _key;
    private readonly object?[] _args;
    private readonly IReadOnlyList<LocalText>? _parts;

    private LocalText(string key, object?[] args, IReadOnlyList<LocalText>? parts = null)
    {
        _key = key;
        _args = args;
        _parts = parts;
    }

    /// <summary>
    /// The text of resource <paramref name="key"/>, formatted with <paramref name="args"/>.
    /// Pass the key with <c>nameof(Strings.X)</c>, so that the compiler checks it.
    /// </summary>
    public static LocalText Of(string key, params object?[] args) => new(key, args);

    /// <summary>Several texts in a row, separated by a space.</summary>
    public static LocalText Join(IReadOnlyList<LocalText> parts) =>
        parts.Count == 1 ? parts[0] : new(string.Empty, [], [.. parts]);

    /// <summary>The text in <paramref name="culture"/>, for a message to the client.</summary>
    public string In(CultureInfo culture) => Render(culture, culture);

    /// <summary>The text in the user interface language of this thread, for the tray.</summary>
    public string ForUser() => Render(CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);

    /// <summary>The text in English, for the log.</summary>
    public override string ToString() => Render(CultureInfo.InvariantCulture, CultureInfo.InvariantCulture);

    private string Render(CultureInfo language, CultureInfo formatting)
    {
        if (_parts is not null)
        {
            return string.Join(" ", _parts.Select(part => part.Render(language, formatting)));
        }

        var format = Strings.ResourceManager.GetString(_key, language) ?? _key;
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
