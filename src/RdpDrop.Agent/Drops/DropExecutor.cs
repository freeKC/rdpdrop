using Microsoft.VisualBasic.FileIO;
using RdpDrop.Agent.Logging;
using RdpDrop.Agent.Settings;
using RdpDrop.Core.Protocol;
using RdpDrop.Core.Targets;

namespace RdpDrop.Agent.Drops;

/// <summary>
/// Performs the 0.1 drop: finds the target folder, checks that every item can be reached
/// through drive redirection and copies the items with the regular Windows progress
/// and conflict dialogs. Also used as the fallback when nothing in the session accepts a
/// native drop. Call <see cref="Execute"/> on an STA thread.
/// </summary>
internal sealed class DropExecutor
{
    private readonly FileLogger _logger;
    private readonly AgentSettings _settings;
    private readonly DriveRedirectionProbe _probe;
    private readonly CursorTargetResolver _resolver;

    public DropExecutor(FileLogger logger, AgentSettings settings, DriveRedirectionProbe probe, CursorTargetResolver resolver)
    {
        _logger = logger;
        _settings = settings;
        _probe = probe;
        _resolver = resolver;
    }

    public DropResultMessage Execute(DropMessage drop)
    {
        if (drop.Items.Count == 0)
        {
            return Error(drop, null, "The drop contains no items.");
        }

        // Look at the screen first, while the windows are still where the user dropped.
        var point = InputInjector.ToScreen(drop.X, drop.Y);
        var target = _resolver.Find(point.X, point.Y);
        var folder = TargetFolderPolicy.Resolve(target, _settings.Fallback, KnownFolders.Desktop, KnownFolders.Downloads);
        _logger.Info($"Drop {drop.Id}: under the cursor {target.Kind}{(target.FolderPath is null ? string.Empty : " \"" + target.FolderPath + "\"")}; target folder {folder}.");

        var sources = ItemMapper.Map(drop.Items, _probe, out var error);
        if (sources is null)
        {
            return Error(drop, folder, error ?? "The items cannot be dropped.");
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ItemMapper.IsFileError(ex))
        {
            return Error(drop, folder, $"Cannot create the folder {folder}. {ex.Message}");
        }

        return Copy(drop, sources, folder);
    }

    private DropResultMessage Copy(DropMessage drop, IReadOnlyList<MappedItem> sources, string folder)
    {
        var copied = 0;
        try
        {
            foreach (var source in sources)
            {
                var destination = Path.Combine(folder, source.Name);
                _logger.Info($"Drop {drop.Id}: copying {source.Path} to {destination}.");
                if (source.IsDirectory)
                {
                    FileSystem.CopyDirectory(source.Path, destination, UIOption.AllDialogs, UICancelOption.ThrowException);
                }
                else
                {
                    FileSystem.CopyFile(source.Path, destination, UIOption.AllDialogs, UICancelOption.ThrowException);
                }

                copied++;
            }
        }
        catch (OperationCanceledException)
        {
            var text = copied == 0
                ? "The copy was cancelled."
                : $"The copy was cancelled after {copied} of {sources.Count} items.";
            return new DropResultMessage(drop.Id, DropStatus.Cancelled, folder, text);
        }
        catch (Exception ex) when (ItemMapper.IsFileError(ex) || ex is InvalidOperationException)
        {
            var prefix = copied == 0 ? "The copy failed." : $"The copy failed after {copied} of {sources.Count} items.";
            return Error(drop, folder, $"{prefix} {ex.Message}");
        }

        var summary = sources.Count == 1
            ? $"Copied {sources[0].Name} to {folder}"
            : $"Copied {sources.Count} items to {folder}";
        return new DropResultMessage(drop.Id, DropStatus.Ok, folder, summary);
    }

    private static DropResultMessage Error(DropMessage drop, string? folder, string text) =>
        new(drop.Id, DropStatus.Error, folder, text);
}
