using System.Security;
using Microsoft.VisualBasic.FileIO;
using RdpDrop.Agent.Logging;
using RdpDrop.Agent.Settings;
using RdpDrop.Core.Paths;
using RdpDrop.Core.Protocol;
using RdpDrop.Core.Targets;

namespace RdpDrop.Agent.Drops;

/// <summary>
/// Performs one drop: finds the target folder, checks that every item can be reached
/// through drive redirection and copies the items with the regular Windows progress
/// and conflict dialogs. Call <see cref="Execute"/> on an STA thread.
/// </summary>
internal sealed class DropExecutor
{
    private const int MaxNamesInMessage = 5;

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
        var target = _resolver.Find(drop.X, drop.Y);
        var folder = TargetFolderPolicy.Resolve(target, _settings.Fallback, KnownFolders.Desktop, KnownFolders.Downloads);
        _logger.Info($"Drop {drop.Id}: under the cursor {target.Kind}{(target.FolderPath is null ? string.Empty : " \"" + target.FolderPath + "\"")}; target folder {folder}.");

        var check = CheckItems(drop.Items);
        if (check.Rejected.Count > 0)
        {
            return Error(drop, folder, string.Join(" ", check.Rejected));
        }

        if (check.Unreachable.Count > 0)
        {
            return Error(drop, folder, _probe.IsAvailable() ? UnreachableText(check.Unreachable) : NoRedirectionText);
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return Error(drop, folder, $"Cannot create the folder {folder}. {ex.Message}");
        }

        return Copy(drop, check.Sources, folder);
    }

    private DropResultMessage Copy(DropMessage drop, IReadOnlyList<SourceItem> sources, string folder)
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
        catch (Exception ex) when (IsFileError(ex) || ex is InvalidOperationException)
        {
            var prefix = copied == 0 ? "The copy failed." : $"The copy failed after {copied} of {sources.Count} items.";
            return Error(drop, folder, $"{prefix} {ex.Message}");
        }

        var summary = sources.Count == 1
            ? $"Copied {sources[0].Name} to {folder}"
            : $"Copied {sources.Count} items to {folder}";
        return new DropResultMessage(drop.Id, DropStatus.Ok, folder, summary);
    }

    private static ItemCheck CheckItems(IReadOnlyList<DropItem> items)
    {
        var check = new ItemCheck();
        foreach (var item in items)
        {
            if (!ClientPathMapper.TryMapToTsClient(item.ClientPath, out var mapped, out var reason))
            {
                check.Rejected.Add(reason ?? $"\"{item.ClientPath}\" cannot be dropped.");
                continue;
            }

            if (!Exists(mapped, item.IsDirectory))
            {
                check.Unreachable.Add(item.ClientPath);
                continue;
            }

            check.Sources.Add(new SourceItem(mapped, item.IsDirectory, NameOf(mapped)));
        }

        return check;
    }

    private static bool Exists(string path, bool isDirectory)
    {
        try
        {
            return isDirectory ? Directory.Exists(path) : File.Exists(path);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Name of the copy: the last segment of the path, or "Drive C" for a whole drive.
    /// </summary>
    private static string NameOf(string mappedPath)
    {
        var name = Path.GetFileName(mappedPath.TrimEnd('\\'));
        var driveRootLength = ClientPathMapper.TsClientRoot.Length + 1;
        if (mappedPath.TrimEnd('\\').Length <= driveRootLength)
        {
            return "Drive " + mappedPath[ClientPathMapper.TsClientRoot.Length];
        }

        return name;
    }

    private const string NoRedirectionText =
        "The local drives are not redirected to this session, so the dropped files cannot be read. "
        + "In the connection settings, open Local Resources, choose More, select the drives to share, then reconnect.";

    private static string UnreachableText(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1)
        {
            return $"{paths[0]} cannot be reached from the remote session. "
                + "Check that its drive is shared in the Local Resources settings of the connection and that it is still plugged in.";
        }

        var names = string.Join(", ", paths.Take(MaxNamesInMessage));
        if (paths.Count > MaxNamesInMessage)
        {
            names += $" and {paths.Count - MaxNamesInMessage} more";
        }

        return $"{paths.Count} items cannot be reached from the remote session: {names}. "
            + "Check that their drives are shared in the Local Resources settings of the connection and that they are still plugged in.";
    }

    private static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException;

    private static DropResultMessage Error(DropMessage drop, string? folder, string text) =>
        new(drop.Id, DropStatus.Error, folder, text);

    private sealed record SourceItem(string Path, bool IsDirectory, string Name);

    private sealed class ItemCheck
    {
        public List<SourceItem> Sources { get; } = [];

        public List<string> Unreachable { get; } = [];

        public List<string> Rejected { get; } = [];
    }
}
