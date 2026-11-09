using Timer = System.Threading.Timer;
using System.Security;
using UltimateRDP.Agent.Logging;

namespace UltimateRDP.Agent.Drops;

/// <summary>
/// Local copies of dropped files, for targets that cannot read the redirected drive
/// paths. Each drag gets <c>%LOCALAPPDATA%\UltimateRDP\staging\&lt;id&gt;</c>. A folder is
/// deleted one hour after its drop, not right after it, because the target may still be
/// reading the files (Explorer copies on its own thread, a browser reads at upload time),
/// and folders left by an earlier run are deleted when the agent starts.
/// </summary>
internal sealed class StagingFolder : IDisposable
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromHours(1);

    private readonly FileLogger _logger;
    private readonly string _root;
    private readonly List<Timer> _timers = [];
    private readonly Lock _gate = new();

    public StagingFolder(FileLogger logger, string root)
    {
        _logger = logger;
        _root = root;
    }

    /// <summary>Deletes the folders older than <see cref="KeepFor"/>. Called when the agent starts.</summary>
    public void DeleteOldFolders()
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            foreach (var folder in Directory.EnumerateDirectories(_root))
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(folder) > KeepFor)
                {
                    Delete(folder, "older than one hour");
                }
            }
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            _logger.Warn($"Could not list the staging folder {_root}. {ex.Message}");
        }
    }

    /// <summary>
    /// Copies <paramref name="sources"/> into the folder of drag <paramref name="id"/>
    /// and returns the paths of the copies, in the same order. Throws an
    /// <see cref="IOException"/> or another file error when a copy fails; the partial
    /// folder is then scheduled for deletion like any other.
    /// </summary>
    public IReadOnlyList<string> Stage(string id, IReadOnlyList<MappedItem> sources)
    {
        var folder = Path.Combine(_root, SafeName(id));
        Directory.CreateDirectory(folder);
        var staged = new List<string>(sources.Count);
        try
        {
            foreach (var source in sources)
            {
                var destination = Path.Combine(folder, source.Name);
                _logger.Info($"Drag {id}: staging {source.Path} to {destination}.");
                if (source.IsDirectory)
                {
                    CopyDirectory(source.Path, destination);
                }
                else
                {
                    File.Copy(source.Path, destination, overwrite: true);
                }

                staged.Add(destination);
            }
        }
        catch
        {
            ScheduleDelete(id);
            throw;
        }

        return staged;
    }

    /// <summary>Deletes the folder of drag <paramref name="id"/> after <see cref="KeepFor"/>.</summary>
    public void ScheduleDelete(string id) => ScheduleDelete(id, KeepFor);

    /// <summary>Deletes the folder of drag <paramref name="id"/> after <paramref name="delay"/>.</summary>
    private void ScheduleDelete(string id, TimeSpan delay)
    {
        var folder = Path.Combine(_root, SafeName(id));
        lock (_gate)
        {
            Timer? timer = null;
            timer = new Timer(
                _ =>
                {
                    Delete(folder, "one hour after its drop");
                    lock (_gate)
                    {
                        _timers.Remove(timer!);
                    }

                    timer!.Dispose();
                },
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _timers.Add(timer);
            timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var timer in _timers)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }
    }

    /// <summary>
    /// The id comes from the client: only letters, digits and dashes reach the file
    /// system, so it can never name a folder outside the staging root.
    /// </summary>
    public static string SafeName(string id)
    {
        var name = new string([.. id.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(64)]);
        return name.Length > 0 ? name : "drag";
    }

    private void Delete(string folder, string reason)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
                _logger.Info($"Deleted the staging folder {folder} ({reason}).");
            }
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            _logger.Warn($"Could not delete the staging folder {folder}; it is tried again at the next start. {ex.Message}");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException;
}
