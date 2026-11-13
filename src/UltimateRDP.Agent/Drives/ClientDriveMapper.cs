using System.ComponentModel;
using System.Text.Json;
using UltimateRDP.Agent.Drops;
using UltimateRDP.Agent.Interop;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Settings;
using UltimateRDP.Core.Agent;

namespace UltimateRDP.Agent.Drives;

/// <summary>
/// Maps the redirected drives of the client (\\tsclient\C and so on) to drive letters of the
/// session while an UltimateRDP client is connected, when the <c>mapClientDrives</c> setting is
/// <c>auto</c>, and removes them when the client disconnects or the agent exits.
/// </summary>
/// <remarks>
/// <para>
/// A mapping is made with WNetAddConnection2, not remembered and without credentials, so it
/// belongs to the logon session of the user: other users of the server never see it. A drive
/// keeps its own letter when that letter is free in the session, and gets the first free one
/// from Z down otherwise (<see cref="ClientDriveLetters.Choose"/>). A letter in use, a local
/// drive or a mapping of anyone, is never replaced, and a drive that is already mapped
/// somewhere in the session is not mapped again.
/// </para>
/// <para>
/// The removal asks without force first, then with force, and only for the mappings this run
/// of the agent made. They are kept in memory and in a small state file, so that a mapping
/// left by an agent that ended badly is removed at the next start, without force and only when
/// the letter still points to the same share. Every failure is a warning in the log; nothing is
/// ever shown. The work runs on the thread pool, one step after the other, since a call on
/// \\tsclient may wait for the client.
/// </para>
/// </remarks>
internal sealed class ClientDriveMapper
{
    private const int Attempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions StateOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly FileLogger _logger;
    private readonly AgentSettings _settings;
    private readonly string _statePath;
    private readonly Lock _gate = new();
    private readonly List<ClientDriveLetter> _mapped = [];
    private readonly Lazy<DriveRedirectionProbe> _probe;
    private Task _queue = Task.CompletedTask;
    private int _generation;
    private bool _clientPresent;

    public ClientDriveMapper(FileLogger logger, AgentSettings settings, string statePath)
    {
        _logger = logger;
        _settings = settings;
        _statePath = statePath;
        _probe = new Lazy<DriveRedirectionProbe>(() => new DriveRedirectionProbe(logger));
    }

    /// <summary>True when an earlier run left its state file: some mappings may be left.</summary>
    public static bool HasLeftovers(string statePath)
    {
        try
        {
            return File.Exists(statePath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Removes the mappings an earlier run left, before anything else.</summary>
    public void RemoveLeftovers() => Enqueue(RemoveLeftoversNow);

    /// <summary>An UltimateRDP client connected to the session, or left it.</summary>
    public void OnClientPresence(bool present)
    {
        int generation;
        lock (_gate)
        {
            _clientPresent = present;
            generation = ++_generation;
        }

        if (present)
        {
            Enqueue(() => Map(generation));
        }
        else
        {
            Enqueue(() => RemoveAll("the client disconnected", generation));
        }
    }

    /// <summary>The tray menu changed the setting: the mappings follow at once.</summary>
    public void OnModeChanged(MapClientDrivesMode mode)
    {
        int generation;
        bool present;
        lock (_gate)
        {
            present = _clientPresent;
            generation = ++_generation;
        }

        if (mode == MapClientDrivesMode.Auto && present)
        {
            Enqueue(() => Map(generation));
        }
        else if (mode == MapClientDrivesMode.Never)
        {
            Enqueue(() => RemoveAll("the setting was turned off", generation));
        }
    }

    /// <summary>The agent exits: its mappings are removed, waiting at most <paramref name="wait"/>.</summary>
    public void Stop(TimeSpan wait)
    {
        int generation;
        lock (_gate)
        {
            _clientPresent = false;
            generation = ++_generation;
        }

        var last = Enqueue(() => RemoveAll("the agent stops", generation));
        try
        {
            if (!last.Wait(wait))
            {
                _logger.Warn($"The mappings of the client drives were not all removed within {wait.TotalMilliseconds:0} ms; the next start removes what is left.");
            }
        }
        catch (AggregateException ex)
        {
            _logger.Warn($"Removing the mappings of the client drives failed. {ex.InnerException?.Message}");
        }
    }

    private Task Enqueue(Action action)
    {
        lock (_gate)
        {
            _queue = _queue.ContinueWith(_ => Guarded(action), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return _queue;
        }
    }

    private void Guarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // A thread pool error would end the process; the mappings are not worth it.
            _logger.Warn($"The client drives on drive letters failed unexpectedly. {ex.Message}");
        }
    }

    private bool IsCurrent(int generation)
    {
        lock (_gate)
        {
            return generation == _generation;
        }
    }

    private void Map(int generation)
    {
        if (_settings.MapClientDrives != MapClientDrivesMode.Auto || !IsCurrent(generation))
        {
            return;
        }

        IReadOnlyList<char> drives = [];
        for (var attempt = 1; attempt <= Attempts && IsCurrent(generation); attempt++)
        {
            drives = _probe.Value.ListDrives();
            if (drives.Count > 0 || attempt == Attempts)
            {
                break;
            }

            Thread.Sleep(RetryDelay);
        }

        if (!IsCurrent(generation))
        {
            return;
        }

        if (drives.Count == 0)
        {
            _logger.Info("Client drives on drive letters: the session sees no redirected drive of the client; nothing is mapped.");
            return;
        }

        var (taken, alreadyMapped) = LettersInUse();
        var plan = ClientDriveLetters.Choose(drives.Where(drive => !alreadyMapped.Contains(drive)), taken, out var unmapped);
        var done = new List<string>();
        foreach (var letter in plan)
        {
            if (!IsCurrent(generation))
            {
                break;
            }

            var result = Mpr.AddTemporaryConnection(letter.LocalName, letter.RemoteName);
            if (result == Mpr.NoError)
            {
                lock (_gate)
                {
                    _mapped.Add(letter);
                }

                done.Add($"{letter.RemoteName} on {letter.LocalName}");
            }
            else
            {
                _logger.Warn($"Client drives on drive letters: {letter.RemoteName} could not be mapped to {letter.LocalName}, error {result}: {Describe(result)}");
            }
        }

        SaveState();
        _logger.Info($"Client drives on drive letters: {(done.Count == 0 ? "nothing mapped" : string.Join(", ", done))}"
            + (alreadyMapped.Count == 0 ? string.Empty : $"; already mapped in the session: {string.Join(", ", alreadyMapped)}")
            + (unmapped.Count == 0 ? string.Empty : $"; no free letter for {string.Join(", ", unmapped)}")
            + ".");
    }

    /// <summary>
    /// The letters in use in the session, local drives and mappings, and the client drives
    /// that a mapping already reaches, whoever made it.
    /// </summary>
    private static (HashSet<char> Taken, List<char> AlreadyMapped) LettersInUse()
    {
        var taken = new HashSet<char>();
        var alreadyMapped = new List<char>();
        var logical = Kernel32.GetLogicalDrives();
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            if ((logical & (1u << (letter - 'A'))) != 0)
            {
                taken.Add(letter);
            }

            var remote = Mpr.GetConnection(letter + ":", out var result);
            if (result != Mpr.ErrorNotConnected)
            {
                // A mapping, or a remembered one that is not available now: both keep their letter.
                taken.Add(letter);
            }

            if (remote is not null && TsClientDrive(remote) is { } drive)
            {
                alreadyMapped.Add(drive);
            }
        }

        return (taken, alreadyMapped);
    }

    /// <summary>The drive of a share such as \\tsclient\C, or null for any other share.</summary>
    private static char? TsClientDrive(string remote)
    {
        const string Prefix = @"\\tsclient\";
        var rest = remote.TrimEnd('\\');
        return rest.Length == Prefix.Length + 1 && rest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && ClientDriveLetters.IsMappable(char.ToUpperInvariant(rest[^1]))
            ? char.ToUpperInvariant(rest[^1])
            : null;
    }

    private void RemoveAll(string reason, int generation)
    {
        List<ClientDriveLetter> mapped;
        lock (_gate)
        {
            if (generation != _generation && _clientPresent)
            {
                // The client came back meanwhile; the next mapping step decides.
                return;
            }

            mapped = [.. _mapped];
        }

        if (mapped.Count == 0)
        {
            return;
        }

        var removed = new List<string>();
        foreach (var letter in mapped)
        {
            var result = Mpr.CancelConnection(letter.LocalName, force: false);
            if (result != Mpr.NoError && result != Mpr.ErrorNotConnected)
            {
                _logger.Info($"Client drives on drive letters: {letter.LocalName} is in use (error {result}: {Describe(result)}); removing it with force, since this agent made it.");
                result = Mpr.CancelConnection(letter.LocalName, force: true);
            }

            if (result is Mpr.NoError or Mpr.ErrorNotConnected)
            {
                lock (_gate)
                {
                    _mapped.Remove(letter);
                }

                removed.Add(letter.LocalName);
            }
            else
            {
                _logger.Warn($"Client drives on drive letters: {letter.LocalName} ({letter.RemoteName}) could not be removed, error {result}: {Describe(result)}");
            }
        }

        SaveState();
        _logger.Info($"Client drives on drive letters: {string.Join(", ", removed)} removed, {reason}.");
    }

    private void RemoveLeftoversNow()
    {
        StateFile? state;
        try
        {
            state = JsonSerializer.Deserialize<StateFile>(File.ReadAllBytes(_statePath), StateOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _logger.Warn($"Client drives on drive letters: the state file {_statePath} cannot be read; it is deleted. {ex.Message}");
            DeleteState();
            return;
        }

        foreach (var entry in state?.Drives ?? [])
        {
            if (entry.Letter is not { Length: 1 } letterText || entry.Remote is not { Length: > 0 } remote)
            {
                continue;
            }

            var localName = letterText + ":";
            var current = Mpr.GetConnection(localName, out _);
            if (current is null || !string.Equals(current.TrimEnd('\\'), remote.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info($"Client drives on drive letters: {localName} no longer points to {remote}, as an earlier run left it; it is left as it is.");
                continue;
            }

            var result = Mpr.CancelConnection(localName, force: false);
            if (result == Mpr.NoError)
            {
                _logger.Info($"Client drives on drive letters: {localName} ({remote}), left by an earlier run, removed.");
            }
            else
            {
                _logger.Warn($"Client drives on drive letters: {localName} ({remote}), left by an earlier run, could not be removed without force, error {result}: {Describe(result)}");
            }
        }

        DeleteState();
    }

    private void SaveState()
    {
        List<ClientDriveLetter> mapped;
        lock (_gate)
        {
            mapped = [.. _mapped];
        }

        if (mapped.Count == 0)
        {
            DeleteState();
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var state = new StateFile { Drives = [.. mapped.Select(letter => new StateEntry { Letter = letter.Letter.ToString(), Remote = letter.RemoteName })] };
            File.WriteAllBytes(_statePath, JsonSerializer.SerializeToUtf8Bytes(state, StateOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger.Warn($"Client drives on drive letters: the state file {_statePath} cannot be written; a mapping left by a crash would stay until the end of the session. {ex.Message}");
        }
    }

    private void DeleteState()
    {
        try
        {
            File.Delete(_statePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger.Warn($"Client drives on drive letters: the state file {_statePath} cannot be deleted. {ex.Message}");
        }
    }

    private static string Describe(int error) => new Win32Exception(error).Message;

    /// <summary>The mappings this agent made, for the next start.</summary>
    private sealed class StateFile
    {
        public List<StateEntry>? Drives { get; set; }
    }

    private sealed class StateEntry
    {
        public string? Letter { get; set; }

        public string? Remote { get; set; }
    }
}
