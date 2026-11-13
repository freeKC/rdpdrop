namespace UltimateRDP.Core.RemoteApp;

/// <summary>What the client does next to start the program of a RemoteApp session.</summary>
public enum RemoteProgramStep
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Call ServerStartProgram now.</summary>
    CallServerStartProgram,
}

/// <summary>
/// When the client calls ServerStartProgram, since 0.9.0. The first real run of 0.8.0 showed
/// that the RemoteProgram2 values alone never start the program with that server: 0.8.0 waited
/// 10 seconds after the logon, called ServerStartProgram, and the program came 0.4 seconds
/// later. The client now calls it at the logon, unless the control already reported a result
/// or a window of the program, and once more after <see cref="RetryMilliseconds"/> when no
/// result and no window came, at most <see cref="MaxCalls"/> calls in all.
/// </summary>
/// <remarks>Not thread safe: the client calls it on its UI thread.</remarks>
public sealed class RemoteProgramStart
{
    /// <summary>The wait after the first call before the second one.</summary>
    public const int RetryMilliseconds = 10_000;

    /// <summary>Calls of one connection at most.</summary>
    public const int MaxCalls = 2;

    /// <summary>True once the control reported the result of a start.</summary>
    public bool Answered { get; private set; }

    /// <summary>True once a window of the program appeared.</summary>
    public bool WindowShown { get; private set; }

    /// <summary>Calls made for this connection.</summary>
    public int Calls { get; private set; }

    /// <summary>A new connection: nothing of the previous one counts.</summary>
    public void Reset()
    {
        Answered = false;
        WindowShown = false;
        Calls = 0;
    }

    /// <summary>The session is logged on: the first call, unless the program already answered or showed a window.</summary>
    public RemoteProgramStep OnLoggedOn() => Next(0);

    /// <summary>The retry timer elapsed: the second call, when nothing came since the first one.</summary>
    public RemoteProgramStep OnRetryTimer() => Next(1);

    /// <summary>OnRemoteProgramResult came, whatever its result.</summary>
    public void OnResult() => Answered = true;

    /// <summary>A window of the program appeared.</summary>
    public void OnWindow() => WindowShown = true;

    private RemoteProgramStep Next(int callsBefore)
    {
        if (Answered || WindowShown || Calls != callsBefore || Calls >= MaxCalls)
        {
            return RemoteProgramStep.None;
        }

        Calls++;
        return RemoteProgramStep.CallServerStartProgram;
    }
}
