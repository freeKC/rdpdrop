namespace UltimateRDP.Core.RemoteApp;

/// <summary>How the start of a program on a shared connection ended, for its loader.</summary>
public enum JoinedStartEnd
{
    /// <summary>Not yet.</summary>
    None,

    /// <summary>A new window of a program appeared.</summary>
    NewWindow,

    /// <summary>The server answered Ok and no new window came in <see cref="JoinedProgramStart.OkGraceMilliseconds"/>: the program may only have brought its window to the front.</summary>
    AnsweredOk,

    /// <summary>The server answered with a failure.</summary>
    Failed,

    /// <summary>Neither a window nor an answer came in <see cref="JoinedProgramStart.TimeoutMilliseconds"/>.</summary>
    TimedOut,
}

/// <summary>
/// The start of one program on a connection that already runs, since 0.10.0, as the loader of
/// that launch follows it: it begins when ServerStartProgram is called, with the number of
/// windows of programs open at that time, and ends at a new window, at a failure, a short while
/// after an Ok without a new window, or at a time limit. Times come from the caller.
/// </summary>
public sealed class JoinedProgramStart
{
    /// <summary>The wait after an Ok of the server for a new window.</summary>
    public const long OkGraceMilliseconds = 3000;

    /// <summary>The longest wait for a window or an answer.</summary>
    public const long TimeoutMilliseconds = 30_000;

    private long _startedAt;
    private long? _okAt;
    private int _windowsAtStart;

    /// <summary>True once ServerStartProgram was called for the launch.</summary>
    public bool Started { get; private set; }

    /// <summary>How the start ended; <see cref="JoinedStartEnd.None"/> while it runs.</summary>
    public JoinedStartEnd End { get; private set; }

    /// <summary>ServerStartProgram is called, with <paramref name="windows"/> windows of programs open.</summary>
    public void OnStarted(int windows, long nowMilliseconds)
    {
        if (Started || End != JoinedStartEnd.None)
        {
            return;
        }

        Started = true;
        _windowsAtStart = windows;
        _startedAt = nowMilliseconds;
    }

    /// <summary>The windows of programs changed; true when the start ended with it.</summary>
    public bool OnWindows(int windows) => Started && windows > _windowsAtStart && Finish(JoinedStartEnd.NewWindow);

    /// <summary>The server answered for this program; true when the start ended with it.</summary>
    public bool OnResult(bool ok, long nowMilliseconds)
    {
        if (!Started || End != JoinedStartEnd.None)
        {
            return false;
        }

        if (!ok)
        {
            return Finish(JoinedStartEnd.Failed);
        }

        _okAt ??= nowMilliseconds;
        return false;
    }

    /// <summary>A tick of the timer of the client; true when the start ended with it.</summary>
    public bool OnTick(long nowMilliseconds)
    {
        if (!Started || End != JoinedStartEnd.None)
        {
            return false;
        }

        if (_okAt is long ok && nowMilliseconds - ok >= OkGraceMilliseconds)
        {
            return Finish(JoinedStartEnd.AnsweredOk);
        }

        return nowMilliseconds - _startedAt >= TimeoutMilliseconds && Finish(JoinedStartEnd.TimedOut);
    }

    private bool Finish(JoinedStartEnd end)
    {
        if (End != JoinedStartEnd.None)
        {
            return false;
        }

        End = end;
        return true;
    }
}
