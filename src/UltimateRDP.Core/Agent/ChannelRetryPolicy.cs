namespace UltimateRDP.Core.Agent;

/// <summary>
/// What the agent knows after an attempt to open the channel failed, or after the channel
/// closed.
/// </summary>
/// <param name="IsRemoteSession">True when the session is a Remote Desktop session (SM_REMOTESESSION).</param>
/// <param name="IsSessionDisconnected">True when the session has no client at the moment.</param>
/// <param name="OpenError">Win32 error of the failed open, or zero when the channel was open and closed.</param>
/// <param name="ConsecutiveFailures">Failed opens in a row, this one included; zero after a closed channel.</param>
/// <param name="SinceConnect">
/// Time since the agent started or since the session was last connected or logged on.
/// </param>
public readonly record struct ChannelAttempt(
    bool IsRemoteSession,
    bool IsSessionDisconnected,
    int OpenError,
    int ConsecutiveFailures,
    TimeSpan SinceConnect);

/// <summary>
/// Decides how long the agent waits before it tries to open the channel again. The wait
/// is never shorter than <see cref="ShortDelay"/>, so that the loop never spins. It
/// grows to <see cref="LongDelay"/> when no UltimateRDP client can be there: the session
/// is not remote or has no client, the client did not ask for the channel (the case of
/// the Microsoft client), or the same failure keeps coming. Shortly after a connection
/// the short wait applies, because the channel may need a moment to appear. A connection
/// or a logon notification makes the agent try at once, whatever the wait.
/// </summary>
public static class ChannelRetryPolicy
{
    /// <summary>Wait after a closed channel, or after an unexpected failure.</summary>
    public static readonly TimeSpan ShortDelay = TimeSpan.FromSeconds(2);

    /// <summary>Wait while no UltimateRDP client can be connected.</summary>
    public static readonly TimeSpan LongDelay = TimeSpan.FromSeconds(10);

    /// <summary>After a connection, the short wait applies for this long.</summary>
    public static readonly TimeSpan ConnectGracePeriod = TimeSpan.FromSeconds(30);

    /// <summary>From this number of failures in a row, the long wait applies.</summary>
    public const int FailuresBeforeLongDelay = 3;

    /// <summary>ERROR_FILE_NOT_FOUND.</summary>
    public const int ErrorFileNotFound = 2;

    /// <summary>ERROR_NOT_FOUND.</summary>
    public const int ErrorNotFound = 1168;

    /// <summary>True when the open failed because the channel does not exist in the session.</summary>
    public static bool IsNotFound(int error) => error is ErrorFileNotFound or ErrorNotFound;

    /// <summary>
    /// True when the failure is the normal state of a session without an UltimateRDP
    /// client, and so not worth a warning in the log.
    /// </summary>
    public static bool IsExpected(ChannelAttempt attempt) =>
        !attempt.IsRemoteSession || attempt.IsSessionDisconnected || IsNotFound(attempt.OpenError);

    /// <summary>The wait before the next attempt.</summary>
    public static TimeSpan NextDelay(ChannelAttempt attempt)
    {
        if (!attempt.IsRemoteSession || attempt.IsSessionDisconnected)
        {
            return LongDelay;
        }

        if (attempt.SinceConnect < ConnectGracePeriod)
        {
            return ShortDelay;
        }

        if (IsNotFound(attempt.OpenError) || attempt.ConsecutiveFailures >= FailuresBeforeLongDelay)
        {
            return LongDelay;
        }

        return ShortDelay;
    }
}
