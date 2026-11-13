namespace UltimateRDP.Core.Agent;

/// <summary>The shell of a session, as its processes show it.</summary>
public enum SessionShell
{
    /// <summary>Neither shell runs yet, or a shell of another kind runs.</summary>
    Unknown,

    /// <summary>Explorer runs: a full desktop session.</summary>
    Desktop,

    /// <summary>The RemoteApp shell (rdpinit or rdpshell) runs and Explorer does not.</summary>
    RemoteApp,
}

/// <summary>What the agent does after a check of a RemoteApp session.</summary>
public enum RemoteAppVerdict
{
    /// <summary>An application still runs in the session, or the check must be confirmed.</summary>
    Stay,

    /// <summary>No application is left, but a drag or a transfer runs: the next check decides.</summary>
    StayWhileBusy,

    /// <summary>
    /// No application ran yet and the agent started a short time ago: the program of the
    /// session may still be starting, so the check does not count.
    /// </summary>
    StayAtStart,

    /// <summary>No application is left: the agent exits, so that the session can end.</summary>
    Exit,
}

/// <summary>
/// The rules that keep the agent from holding a RemoteApp session open. Such a session ends
/// when no process of the user is left in it but the ones Windows ignores: its shell, the
/// processes of every session, and the names listed under the SysProcs key of the server. An
/// agent that keeps running would keep the session alive after the user closed the program,
/// so the agent leaves once nothing else is left. Pure functions; the agent reads the
/// processes and the registry.
/// </summary>
public static class RemoteAppSessionRule
{
    /// <summary>The time between two checks of a RemoteApp session.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the agent looks for the shell of its session after it starts, at the pace
    /// of <see cref="CheckInterval"/>: a task started at logon may run before the shell.
    /// </summary>
    public static readonly TimeSpan ClassifyPeriod = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Checks in a row with no application left before the agent exits: two checks
    /// <see cref="CheckInterval"/> apart, more than the 20 seconds a RemoteApp session waits
    /// after its last window closed.
    /// </summary>
    public const int EmptyChecksToExit = 2;

    /// <summary>
    /// How long after its start the agent waits for a first application before an empty
    /// check counts: a task started at logon runs before the program of the session, and a
    /// busy server may take a while to start it. A disconnected session still makes the
    /// agent exit at once, so this wait cannot hold a session that nobody uses.
    /// </summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The processes that run in every RemoteApp session whatever the program, as a survey
    /// of a production server showed them, in the form <see cref="Normalize"/> gives. They
    /// never count as an application. The agent itself is added by
    /// <see cref="Applications"/>, and the SysProcs names of the server too.
    /// </summary>
    public static IReadOnlyCollection<string> SessionProcesses { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "rdpinit",
        "rdpshell",
        "winlogon",
        "csrss",
        "dwm",
        "fontdrvhost",
        "sihost",
        "svchost",
        "taskhostw",
        "ctfmon",
        "rdpclip",
        "tabtip",
        "tabtip32",
        "defendersessionhelper",
        "logonui",
        "runtimebroker",
        "searchapp",
        "shellexperiencehost",
        "conhost",
    };

    private const string Explorer = "explorer";
    private const string RdpInit = "rdpinit";
    private const string RdpShell = "rdpshell";

    /// <summary>
    /// A process name in the form the rules compare: without folder, without a final
    /// <c>.exe</c>, trimmed and in lower case. Windows compares image names without case.
    /// </summary>
    public static string Normalize(string? name)
    {
        var value = (name ?? string.Empty).Trim();
        var slash = value.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            value = value[(slash + 1)..];
        }

        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        return value.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// The shell of a session from the names of its processes: Explorer makes a desktop
    /// session, even when a RemoteApp shell runs too; rdpinit or rdpshell without Explorer
    /// make a RemoteApp session; neither leaves it unknown.
    /// </summary>
    public static SessionShell Classify(IEnumerable<string> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);
        var remoteAppShell = false;
        foreach (var name in processNames)
        {
            var normalized = Normalize(name);
            if (normalized == Explorer)
            {
                return SessionShell.Desktop;
            }

            remoteAppShell |= normalized is RdpInit or RdpShell;
        }

        return remoteAppShell ? SessionShell.RemoteApp : SessionShell.Unknown;
    }

    /// <summary>
    /// The processes of the session that keep it open: every one but the
    /// <see cref="SessionProcesses"/>, the <paramref name="sysProcs"/> names of the server
    /// and the agent itself (<paramref name="ownName"/>). Each name once, as the session
    /// gave it, in the order of the names.
    /// </summary>
    public static IReadOnlyList<string> Applications(IEnumerable<string> processNames, IEnumerable<string> sysProcs, string ownName)
    {
        ArgumentNullException.ThrowIfNull(processNames);
        ArgumentNullException.ThrowIfNull(sysProcs);
        var ignored = new HashSet<string>(sysProcs.Select(Normalize), StringComparer.Ordinal) { Normalize(ownName) };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var name in processNames)
        {
            var normalized = Normalize(name);
            if (normalized.Length == 0 || SessionProcesses.Contains(normalized) || ignored.Contains(normalized) || !seen.Add(normalized))
            {
                continue;
            }

            result.Add(name.Trim());
        }

        return result;
    }
}

/// <summary>The outcome of one check of a RemoteApp session.</summary>
/// <param name="Verdict">What the agent does.</param>
/// <param name="EmptyChecks">The checks in a row that found no application, this one included.</param>
/// <param name="Applications">The applications found, as <see cref="RemoteAppSessionRule.Applications"/> gives them.</param>
/// <param name="Changed">
/// True when the verdict or the applications differ from the previous check: the agent logs a
/// line then, and only then, so that a session that runs for days does not fill the log.
/// </param>
public sealed record RemoteAppCheck(RemoteAppVerdict Verdict, int EmptyChecks, IReadOnlyList<string> Applications, bool Changed)
{
    /// <summary>Readable form for the log.</summary>
    public string Describe() => Verdict switch
    {
        RemoteAppVerdict.Exit => FormattableString.Invariant($"no application is left after {EmptyChecks} check(s)"),
        RemoteAppVerdict.StayWhileBusy => FormattableString.Invariant($"no application is left after {EmptyChecks} check(s), but a drag or a transfer runs"),
        RemoteAppVerdict.StayAtStart => FormattableString.Invariant($"no application runs yet; the agent waits {RemoteAppSessionRule.StartGrace.TotalSeconds:0} s after its start before it counts the checks"),
        _ when Applications.Count == 0 => FormattableString.Invariant($"no application is left at check {EmptyChecks} of {RemoteAppSessionRule.EmptyChecksToExit}"),
        _ => "applications running: " + string.Join(", ", Applications),
    };
}

/// <summary>
/// Follows the checks of one RemoteApp session: the agent exits after
/// <see cref="RemoteAppSessionRule.EmptyChecksToExit"/> checks in a row without application
/// while no drag or transfer runs, or at once when the session is disconnected without
/// application. Not thread safe: one thread runs the checks.
/// </summary>
public sealed class RemoteAppExitWatch
{
    private int _emptyChecks;
    private RemoteAppVerdict? _lastVerdict;
    private IReadOnlyList<string> _lastApplications = [];
    private bool _seenApplication;

    /// <summary>
    /// A regular check, with the applications found, whether a drag or a transfer runs, and
    /// the time since the agent started when it is known. Until a first application was
    /// seen, an empty check within <see cref="RemoteAppSessionRule.StartGrace"/> of the
    /// start does not count.
    /// </summary>
    public RemoteAppCheck Check(IReadOnlyList<string> applications, bool busy, TimeSpan? sinceStart = null)
    {
        ArgumentNullException.ThrowIfNull(applications);
        RemoteAppVerdict verdict;
        if (applications.Count > 0)
        {
            _seenApplication = true;
            _emptyChecks = 0;
            verdict = RemoteAppVerdict.Stay;
        }
        else if (!_seenApplication && sinceStart is { } elapsed && elapsed < RemoteAppSessionRule.StartGrace)
        {
            _emptyChecks = 0;
            verdict = RemoteAppVerdict.StayAtStart;
        }
        else
        {
            _emptyChecks++;
            verdict = _emptyChecks < RemoteAppSessionRule.EmptyChecksToExit
                ? RemoteAppVerdict.Stay
                : busy ? RemoteAppVerdict.StayWhileBusy : RemoteAppVerdict.Exit;
        }

        return Record(verdict, applications);
    }

    /// <summary>
    /// The session was disconnected: the agent exits at once when no application is left,
    /// since no client can finish a drag any more. The count of empty checks does not change.
    /// </summary>
    public RemoteAppCheck OnDisconnected(IReadOnlyList<string> applications)
    {
        ArgumentNullException.ThrowIfNull(applications);
        var verdict = applications.Count == 0 ? RemoteAppVerdict.Exit : RemoteAppVerdict.Stay;
        return Record(verdict, applications);
    }

    private RemoteAppCheck Record(RemoteAppVerdict verdict, IReadOnlyList<string> applications)
    {
        var changed = verdict != _lastVerdict
            || !applications.SequenceEqual(_lastApplications, StringComparer.OrdinalIgnoreCase)
            || (applications.Count == 0 && _emptyChecks == 1 && verdict != RemoteAppVerdict.StayAtStart);
        _lastVerdict = verdict;
        _lastApplications = [.. applications];
        return new RemoteAppCheck(verdict, applications.Count == 0 ? Math.Max(1, _emptyChecks) : 0, _lastApplications, changed);
    }
}
