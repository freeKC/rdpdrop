namespace UltimateRDP.Core.RemoteApp;

/// <summary>What the loader of a start shows, since 0.9.3.</summary>
public enum LoaderStep
{
    /// <summary>The process starts, or the .rdp file is read: "Starting...".</summary>
    Starting,

    /// <summary>The control connects to the server: "Connecting to {server}...".</summary>
    Connecting,

    /// <summary>The control is connected and the logon runs: "Opening the session...".</summary>
    OpeningSession,

    /// <summary>A RemoteApp session is logged on and its program starts: "Starting {program}...".</summary>
    StartingProgram,

    /// <summary>The start is over, one way or another: the loader closes.</summary>
    Closed,
}

/// <summary>An event of a start, as the client sees it.</summary>
public enum LoaderEvent
{
    /// <summary>Connect was called: the control connects.</summary>
    Connecting,

    /// <summary>OnConnected.</summary>
    Connected,

    /// <summary>OnLoginComplete.</summary>
    LoggedOn,

    /// <summary>The first window of the RemoteApp program appeared, from the event of the control or from the windows found in the process.</summary>
    FirstProgramWindow,

    /// <summary>The start ended before it was over: a failure, a disconnection, a cancel or the close of the tab.</summary>
    Ended,
}

/// <summary>
/// The steps of the loader of one start, since 0.9.3: the events of the connection come in, the
/// step and the key of its text come out, and when the loader closes. A desktop start is over
/// at the logon, a RemoteApp start at the first window of its program; any end before that
/// closes the loader too. Steps only go forward. The notice that the server takes longer than
/// usual is due <see cref="SlowNoticeMilliseconds"/> after the loader appeared.
/// </summary>
/// <remarks>Not thread safe: the client uses it on its UI thread. Times come from the caller, so that nothing depends on a clock.</remarks>
public sealed class LoaderProgress
{
    /// <summary>The time after which the loader says that the server takes longer than usual.</summary>
    public const long SlowNoticeMilliseconds = 10_000;

    private readonly long _startMilliseconds;

    /// <param name="startMilliseconds">When the loader appeared, on the clock the caller gives to <see cref="ShowsSlowNotice"/>.</param>
    public LoaderProgress(long startMilliseconds)
    {
        _startMilliseconds = startMilliseconds;
    }

    /// <summary>Where the start stands.</summary>
    public LoaderStep Step { get; private set; } = LoaderStep.Starting;

    /// <summary>True once the start is over and the loader closes.</summary>
    public bool IsClosed => Step == LoaderStep.Closed;

    /// <summary>The host of the session, once the .rdp file was read.</summary>
    public string? Server { get; private set; }

    /// <summary>The name of the RemoteApp program, or null for a desktop.</summary>
    public string? Program { get; private set; }

    /// <summary>True for a RemoteApp start, once the .rdp file was read.</summary>
    public bool IsRemoteApp => Program is not null;

    /// <summary>What is opening, under the name of the application: the program, or the server of a desktop; null before the file is read.</summary>
    public string? Title => Program ?? Server;

    /// <summary>Why the loader closed, in English, for the log; null while it shows.</summary>
    public string? CloseReason { get; private set; }

    /// <summary>The name of the resource of the step text: LoaderStarting, LoaderConnecting, LoaderOpeningSession or LoaderStartingProgram.</summary>
    public string StepTextKey => Step switch
    {
        LoaderStep.Connecting => "LoaderConnecting",
        LoaderStep.OpeningSession => "LoaderOpeningSession",
        LoaderStep.StartingProgram => "LoaderStartingProgram",
        _ => "LoaderStarting",
    };

    /// <summary>The argument of the step text: the server while connecting, the program while it starts; null otherwise.</summary>
    public string? StepTextArgument => Step switch
    {
        LoaderStep.Connecting => Server ?? string.Empty,
        LoaderStep.StartingProgram => Program ?? string.Empty,
        _ => null,
    };

    /// <summary>The .rdp file was read: the server, and the program of a RemoteApp file.</summary>
    public void FileRead(string server, string? program)
    {
        Server = server;
        Program = string.IsNullOrWhiteSpace(program) ? null : program;
    }

    /// <summary>Applies an event; true when the step changed.</summary>
    public bool On(LoaderEvent loaderEvent)
    {
        if (IsClosed)
        {
            return false;
        }

        var next = loaderEvent switch
        {
            LoaderEvent.Connecting when Step == LoaderStep.Starting => LoaderStep.Connecting,
            LoaderEvent.Connected when Step is LoaderStep.Starting or LoaderStep.Connecting => LoaderStep.OpeningSession,
            LoaderEvent.LoggedOn when Step is not LoaderStep.StartingProgram => IsRemoteApp ? LoaderStep.StartingProgram : LoaderStep.Closed,
            LoaderEvent.FirstProgramWindow when IsRemoteApp => LoaderStep.Closed,
            LoaderEvent.Ended => LoaderStep.Closed,
            _ => Step,
        };

        if (next == Step)
        {
            return false;
        }

        if (next == LoaderStep.Closed)
        {
            CloseReason = loaderEvent switch
            {
                LoaderEvent.LoggedOn => "the desktop is logged on",
                LoaderEvent.FirstProgramWindow => "the first window of the program appeared",
                _ => "the start ended before it was over",
            };
        }

        Step = next;
        return true;
    }

    /// <summary>True when the loader still shows <see cref="SlowNoticeMilliseconds"/> or more after it appeared.</summary>
    public bool ShowsSlowNotice(long nowMilliseconds) => !IsClosed && nowMilliseconds - _startMilliseconds >= SlowNoticeMilliseconds;
}
