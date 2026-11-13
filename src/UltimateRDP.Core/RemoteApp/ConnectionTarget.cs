using UltimateRDP.Core.Credentials;
using UltimateRDP.Core.RdpFiles;

namespace UltimateRDP.Core.RemoteApp;

/// <summary>
/// Where a connection goes and with which account, since 0.10.0: two launches with the same
/// target share one connection, since Windows gives a user one session per server and a second
/// connection with the same account takes the session of the first (disconnection reason 3,
/// extended reason 5).
/// </summary>
/// <param name="Host">The host of <c>full address</c>, as the .rdp file gives it, without the port.</param>
/// <param name="Port">The port: the one of <c>full address</c>, else <c>server port</c>, else 3389.</param>
/// <param name="Gateway">The gateway when the file uses one (<c>gatewayusagemethod</c> 1 or 2), else null.</param>
/// <param name="User">The user name, without its domain; empty when the file gives none.</param>
/// <param name="Domain">The domain, from <c>domain</c> or from a <c>DOMAIN\user</c> name; empty when none.</param>
public sealed record ConnectionTarget(string Host, int Port, string? Gateway, string User, string Domain)
{
    /// <summary>The port when the file gives none.</summary>
    public const int DefaultPort = 3389;

    /// <summary>The target of the settings of <paramref name="file"/>.</summary>
    public static ConnectionTarget FromRdpFile(RdpFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var (host, addressPort) = RdpAddress.Split(file.FullAddress ?? string.Empty);
        var (user, splitDomain) = AccountName.Split(file.UserName);
        var domain = file.Domain?.Trim() is { Length: > 0 } given ? given : splitDomain;
        var gateway = file.GatewayUsageMethod is 1 or 2 && file.GatewayHostname?.Trim() is { Length: > 0 } gatewayHost ? gatewayHost : null;
        return new ConnectionTarget(host.Trim(), addressPort ?? file.ServerPort ?? DefaultPort, gateway, user, domain);
    }

    /// <summary>
    /// True when a launch for <paramref name="launch"/> may use the connection of
    /// <paramref name="existing"/>: the same host, port and gateway, without case, and the same
    /// account. A launch without a user takes the account of the existing connection; two
    /// different users never match. Domains are compared when both are given.
    /// </summary>
    public static bool Matches(ConnectionTarget existing, ConnectionTarget launch)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(launch);
        if (!Same(existing.Host, launch.Host) || existing.Port != launch.Port || !Same(existing.Gateway ?? string.Empty, launch.Gateway ?? string.Empty))
        {
            return false;
        }

        if (launch.User.Length == 0)
        {
            return true;
        }

        return Same(existing.User, launch.User)
            && (existing.Domain.Length == 0 || launch.Domain.Length == 0 || Same(existing.Domain, launch.Domain));
    }

    /// <summary>The target in the log, without the account: host, port and gateway.</summary>
    public override string ToString() =>
        $"{Host}:{Port}{(Gateway is null ? string.Empty : " through " + Gateway)} as {(User.Length == 0 ? "the user Windows asks for" : (Domain.Length == 0 ? User : Domain + "\\" + User))}";

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
