namespace UltimateRDP.Core.RemoteApp;

/// <summary>
/// How a disconnection of the Remote Desktop control ended, from its reason and its
/// extended reason. Since 0.9.0, the ends that a user or Windows causes on purpose are
/// normal: a RemoteApp session that Windows ends 15 to 20 seconds after the last window of
/// its program closed comes with reason 2 and extended reason 11, which 0.8.0 showed as an
/// internal error.
/// </summary>
public static class SessionEnd
{
    /// <summary>disconnectReasonLocalNotError: the client disconnected.</summary>
    public const int LocalNotError = 1;

    /// <summary>disconnectReasonRemoteByUser: the user ended the session on the server, or Windows for them.</summary>
    public const int RemoteByUser = 2;

    /// <summary>disconnectReasonByServer: the server disconnected.</summary>
    public const int ByServer = 3;

    /// <summary>
    /// True when the session ended on purpose: reason 1, 2 or 3 with no extended reason, or
    /// one of 1 (disconnected by a program of the server), 2 (logged off by a program of the
    /// server), 11 (disconnected by the user, as Windows does at the end of a RemoteApp
    /// session) and 12 (logged off by the user).
    /// </summary>
    public static bool IsNormal(int reason, uint extended) =>
        reason is LocalNotError or RemoteByUser or ByServer && extended is 0 or 1 or 2 or 11 or 12;

    /// <summary>The extended reason in words, for the log.</summary>
    public static string DescribeExtended(uint extended) => extended switch
    {
        0 => "no extended reason",
        1 => "disconnected by a program of the server",
        2 => "logged off by a program of the server",
        3 => "idle time limit of the server",
        4 => "logon time limit of the server",
        5 => "replaced by another connection",
        11 => "disconnected by the user, as at the end of a RemoteApp session",
        12 => "logged off by the user",
        _ => "extended reason " + extended.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
