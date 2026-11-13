namespace UltimateRDP.Core.RemoteApp;

/// <summary>What happens to a RemoteApp launch that finds a connection to its target.</summary>
public enum JoinDecision
{
    /// <summary>The connection is logged on: ServerStartProgram for the launch now.</summary>
    StartNow,

    /// <summary>The connection still connects or logs on: the launch waits for the logon.</summary>
    Queued,

    /// <summary>The connection ended: the launch opens its own connection.</summary>
    OpenOwn,
}

/// <summary>Where the connection of a <see cref="SharedRemoteAppConnection{TLaunch}"/> stands.</summary>
public enum SharedConnectionPhase
{
    /// <summary>Connect was called; the logon is not complete.</summary>
    Connecting,

    /// <summary>The session is logged on.</summary>
    LoggedOn,

    /// <summary>The connection ended; it takes no launch any more.</summary>
    Ended,
}

/// <summary>
/// The launches that share one RemoteApp connection, since 0.10.0, as mstsc shares a RemoteApp
/// session: a second launch to the same target (<see cref="ConnectionTarget.Matches"/>) starts
/// its program on the connection that runs instead of opening a second one, which would take
/// the session of the first. A launch that comes while the connection connects waits for the
/// logon; one that comes while it is logged on, even with no window of a program left, starts
/// at once; one that comes after the end opens its own connection. When the connection ends,
/// the launches that wait, and those started whose program never showed, open their own
/// connection, unless the user closed the connection.
/// </summary>
/// <typeparam name="TLaunch">What the client keeps of a launch.</typeparam>
/// <remarks>Not thread safe: the client uses it on its UI thread.</remarks>
public sealed class SharedRemoteAppConnection<TLaunch>
    where TLaunch : class
{
    private readonly List<TLaunch> _queued = [];
    private readonly List<TLaunch> _unconfirmed = [];
    private readonly List<TLaunch> _joined = [];

    /// <summary>Where the connection stands.</summary>
    public SharedConnectionPhase Phase { get; private set; } = SharedConnectionPhase.Connecting;

    /// <summary>The launches that joined the connection, in order, those that wait included.</summary>
    public IReadOnlyList<TLaunch> Joined => _joined;

    /// <summary>The launches that wait for the logon, in order.</summary>
    public IReadOnlyList<TLaunch> Queued => _queued;

    /// <summary>A launch finds the connection: what it does.</summary>
    public JoinDecision Join(TLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        switch (Phase)
        {
            case SharedConnectionPhase.LoggedOn:
                _joined.Add(launch);
                _unconfirmed.Add(launch);
                return JoinDecision.StartNow;
            case SharedConnectionPhase.Connecting:
                _joined.Add(launch);
                _queued.Add(launch);
                return JoinDecision.Queued;
            default:
                return JoinDecision.OpenOwn;
        }
    }

    /// <summary>A new connection starts on the same tab (a reconnection): it takes launches again.</summary>
    public void OnConnecting()
    {
        if (Phase == SharedConnectionPhase.Ended)
        {
            Phase = SharedConnectionPhase.Connecting;
            _joined.Clear();
        }
    }

    /// <summary>The session is logged on: the launches that waited, to start now in their order, after the program of the connection.</summary>
    public IReadOnlyList<TLaunch> OnLoggedOn()
    {
        if (Phase != SharedConnectionPhase.Connecting)
        {
            return [];
        }

        Phase = SharedConnectionPhase.LoggedOn;
        var start = _queued.ToList();
        _queued.Clear();
        _unconfirmed.AddRange(start);
        return start;
    }

    /// <summary>The program of <paramref name="launch"/> showed a window or reported a result: it no longer needs a connection of its own if this one ends.</summary>
    public void Confirm(TLaunch launch) => _unconfirmed.Remove(launch);

    /// <summary>
    /// The connection ended. Returns the launches that open their own connection: those that
    /// waited, then those started without a window or a result, unless
    /// <paramref name="closedByUser"/>, when they are dropped.
    /// </summary>
    public IReadOnlyList<TLaunch> OnEnded(bool closedByUser)
    {
        if (Phase == SharedConnectionPhase.Ended)
        {
            return [];
        }

        Phase = SharedConnectionPhase.Ended;
        var orphans = closedByUser ? [] : _queued.Concat(_unconfirmed).ToList();
        _queued.Clear();
        _unconfirmed.Clear();
        return orphans;
    }
}
