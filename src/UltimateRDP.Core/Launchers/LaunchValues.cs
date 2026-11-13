using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace UltimateRDP.Core.Launchers;

/// <summary>What a value of the command line must be; each rule has a text for the user in the client.</summary>
public enum LaunchValueRule
{
    /// <summary>A host name, an IPv4 address or an IPv6 address, with or without brackets.</summary>
    Host,

    /// <summary>A number from 1 to 65535.</summary>
    Port,

    /// <summary>A text of at most <see cref="LaunchValues.MaxNameLength"/> characters, without quotes or control characters.</summary>
    Name,

    /// <summary>An alias of a published program or a path on the server, without quotes or control characters.</summary>
    Program,

    /// <summary>A text of at most <see cref="LaunchValues.MaxArgumentsLength"/> characters, without control characters.</summary>
    Arguments,

    /// <summary><c>on</c> or <c>off</c>.</summary>
    OnOff,

    /// <summary>A percentage from 100 to 500.</summary>
    Scale,

    /// <summary><c>local</c>, <c>remote</c> or <c>fullscreen</c>.</summary>
    WindowsKeys,

    /// <summary><c>local</c>, <c>remote</c> or <c>off</c>.</summary>
    Audio,
}

/// <summary>
/// The checks of the values of a connection given on the command line or in the launcher
/// generator. A value is never trimmed or changed here: one with spaces around it is refused,
/// so that what a launcher writes is exactly what the client reads back.
/// </summary>
public static class LaunchValues
{
    /// <summary>Longest host, in characters, brackets of an IPv6 address included.</summary>
    public const int MaxHostLength = 255;

    /// <summary>Longest user name, domain or display name, in characters.</summary>
    public const int MaxNameLength = 256;

    /// <summary>Longest program alias or path, in characters.</summary>
    public const int MaxProgramLength = 1024;

    /// <summary>Longest arguments of a program, in characters.</summary>
    public const int MaxArgumentsLength = 2048;

    /// <summary>The port used when none is given.</summary>
    public const int DefaultPort = 3389;

    /// <summary>The smallest scale factor, in percent.</summary>
    public const int MinScale = 100;

    /// <summary>The largest scale factor, in percent.</summary>
    public const int MaxScale = 500;

    /// <summary>
    /// True for a host name (labels of letters, digits, <c>-</c> and <c>_</c> separated by
    /// dots, at most 253 characters), an IPv4 address in four parts, or an IPv6 address,
    /// bare or in brackets.
    /// </summary>
    public static bool IsHost(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxHostLength)
        {
            return false;
        }

        if (value.StartsWith('['))
        {
            return value.Length > 2 && value.EndsWith(']') && IsIPv6(value[1..^1]);
        }

        if (value.Contains(':', StringComparison.Ordinal))
        {
            return IsIPv6(value);
        }

        if (value.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            return value.Split('.').Length == 4
                && IPAddress.TryParse(value, out var address)
                && address.AddressFamily == AddressFamily.InterNetwork;
        }

        return value.Length <= 253 && value.Split('.').All(IsLabel);
    }

    /// <summary>True for a port from 1 to 65535.</summary>
    public static bool IsPort(int value) => value is > 0 and <= 65535;

    /// <summary>True for a scale factor from 100 to 500 percent.</summary>
    public static bool IsScale(int value) => value is >= MinScale and <= MaxScale;

    /// <summary>True for a user name, a domain or a display name. It cannot start with <c>--</c>, which reads as an option.</summary>
    public static bool IsName(string? value) => IsText(value, MaxNameLength, allowQuotes: false) && !IsOptionLike(value!);

    /// <summary>
    /// True for a program: a published alias written <c>||Name</c> or <c>Name</c>, or a path
    /// on the server (see <see cref="IsProgramPath"/>). A <c>|</c> is only accepted in the
    /// <c>||</c> of an alias, and it cannot start with <c>--</c>.
    /// </summary>
    public static bool IsProgram(string? value)
    {
        if (!IsText(value, MaxProgramLength, allowQuotes: false) || IsOptionLike(value!))
        {
            return false;
        }

        var rest = value!.StartsWith(RdpFiles.RemoteAppLaunch.AliasPrefix, StringComparison.Ordinal) ? value[RdpFiles.RemoteAppLaunch.AliasPrefix.Length..] : value;
        return rest.Length > 0 && !rest.Contains('|', StringComparison.Ordinal) && (rest.Length == value.Length || !IsProgramPath(rest));
    }

    /// <summary>
    /// The rule between an alias and a path: a value that starts with <c>||</c> is an alias;
    /// otherwise a value with <c>\</c>, <c>/</c> or <c>:</c> is a path on the server, and
    /// anything else is an alias.
    /// </summary>
    public static bool IsProgramPath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return !value.StartsWith(RdpFiles.RemoteAppLaunch.AliasPrefix, StringComparison.Ordinal) && value.IndexOfAny(['\\', '/', ':']) >= 0;
    }

    /// <summary>
    /// The value of <c>remoteapplicationprogram</c> for <paramref name="program"/>: a path as
    /// it is, an alias with its <c>||</c>.
    /// </summary>
    public static string RdpProgram(string program)
    {
        ArgumentNullException.ThrowIfNull(program);
        return IsProgramPath(program) || program.StartsWith(RdpFiles.RemoteAppLaunch.AliasPrefix, StringComparison.Ordinal)
            ? program
            : RdpFiles.RemoteAppLaunch.AliasPrefix + program;
    }

    /// <summary>True for the arguments of a program: quotes, and a <c>--</c> at the start, are allowed there.</summary>
    public static bool IsArguments(string? value) => IsText(value, MaxArgumentsLength, allowQuotes: true);

    /// <summary>
    /// True for a non empty text of at most <paramref name="maxLength"/> characters, without
    /// spaces at its ends, control characters (line breaks and tabs included), the line and
    /// paragraph separators U+2028 and U+2029, lone
    /// surrogates, and, unless <paramref name="allowQuotes"/>, double quotes.
    /// </summary>
    public static bool IsText(string? value, int maxLength, bool allowQuotes)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c) || (c == '"' && !allowQuotes) || c is '\u2028' or '\u2029')
            {
                return false;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads <c>on</c> or <c>off</c>, without case.</summary>
    public static bool? ParseOnOff(string value) =>
        value.Equals("on", StringComparison.OrdinalIgnoreCase) ? true
        : value.Equals("off", StringComparison.OrdinalIgnoreCase) ? false
        : null;

    /// <summary>The text of a switch on the command line.</summary>
    public static string OnOff(bool value) => value ? "on" : "off";

    /// <summary>Reads a decimal number without sign or spaces.</summary>
    public static int? ParseNumber(string value) =>
        value.Length is > 0 and <= 9 && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    /// <summary>True for a value that starts with <c>--</c>: only the arguments of a program can.</summary>
    public static bool IsOptionLike(string value) => value.StartsWith("--", StringComparison.Ordinal);

    private static bool IsIPv6(string text) =>
        text.Length > 0
        && text.All(c => char.IsAsciiHexDigit(c) || c is ':' or '.')
        && IPAddress.TryParse(text, out var address)
        && address.AddressFamily == AddressFamily.InterNetworkV6;

    private static bool IsLabel(string label) =>
        label.Length is > 0 and <= 63
        && label[0] != '-'
        && label[^1] != '-'
        && label.All(c => !char.IsSurrogate(c) && (char.IsLetterOrDigit(c) || c is '-' or '_'));
}
