using UltimateRDP.Agent.Logging;
using UltimateRDP.Core.Paths;

namespace UltimateRDP.Agent.Drops;

/// <summary>
/// Tells whether at least one client drive is redirected, that is whether one of
/// \\tsclient\C through \\tsclient\Z exists.
/// </summary>
/// <remarks>
/// A lookup on \\tsclient can hang when the client is slow or gone, so the check runs
/// on worker threads and the caller waits at most <see cref="Timeout"/>; a check that
/// does not finish in time counts as "not available". The result is kept for
/// <see cref="CacheDuration"/>. Thread safe.
/// </remarks>
internal sealed class DriveRedirectionProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly FileLogger _logger;
    private Task<bool>? _running;
    private bool _cachedResult;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public DriveRedirectionProbe(FileLogger logger)
    {
        _logger = logger;
    }

    public bool IsAvailable()
    {
        Task<bool> task;
        lock (_gate)
        {
            if (DateTime.UtcNow - _cachedAtUtc < CacheDuration)
            {
                return _cachedResult;
            }

            // Reuse a check that is still running instead of starting another one.
            task = _running ??= Task.Factory.StartNew(Probe, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        var finished = task.Wait(Timeout);
        lock (_gate)
        {
            if (!finished)
            {
                _logger.Warn($"Checking the redirected drives took more than {Timeout.TotalSeconds:0} seconds; treating them as unavailable.");
                return false;
            }

            if (ReferenceEquals(_running, task))
            {
                _running = null;
                _cachedResult = task.Result;
                _cachedAtUtc = DateTime.UtcNow;
                _logger.Debug($"Drive redirection available: {_cachedResult}.");
            }

            return task.Result;
        }
    }

    private static bool Probe()
    {
        // Letters are checked in parallel: a missing one can take a moment to answer.
        return Enumerable.Range('C', 'Z' - 'C' + 1)
            .AsParallel()
            .Any(letter => Exists(ClientPathMapper.TsClientRoot + (char)letter));
    }

    private static bool Exists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
