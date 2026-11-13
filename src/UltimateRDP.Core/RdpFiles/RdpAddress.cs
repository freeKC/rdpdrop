using System.Globalization;

namespace UltimateRDP.Core.RdpFiles;

/// <summary>
/// Splits the <c>full address</c> of a .rdp file into a host and an optional port.
/// Accepts <c>host</c>, <c>host:port</c>, <c>[ipv6]</c>, <c>[ipv6]:port</c> and a bare
/// IPv6 address (several colons, no brackets), which has no port.
/// </summary>
public static class RdpAddress
{
    /// <summary>The host and the port of <paramref name="fullAddress"/>; the port is null when it has none or an invalid one.</summary>
    public static (string Host, int? Port) Split(string fullAddress)
    {
        var text = fullAddress.Trim();
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close > 0)
            {
                var host = text[1..close];
                var rest = text[(close + 1)..];
                return rest.StartsWith(':') && TryPort(rest[1..], out var bracketPort)
                    ? (host, bracketPort)
                    : (host, null);
            }

            return (text, null);
        }

        var first = text.IndexOf(':');
        if (first >= 0 && first == text.LastIndexOf(':'))
        {
            var host = text[..first];
            return TryPort(text[(first + 1)..], out var port) ? (host, port) : (host, null);
        }

        return (text, null);
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
}
