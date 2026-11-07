using System.Security;
using RdpDrop.Core.Paths;
using RdpDrop.Core.Protocol;

namespace RdpDrop.Agent.Drops;

/// <summary>One dropped item, mapped to its path on the redirected drive and checked.</summary>
/// <param name="Path">Path inside the session, for example <c>\\tsclient\C\Users\me\a.txt</c>.</param>
/// <param name="IsDirectory">True for a folder.</param>
/// <param name="Name">Name of the copy: the last segment of the path, or "Drive C" for a whole drive.</param>
internal sealed record MappedItem(string Path, bool IsDirectory, string Name);

/// <summary>
/// Maps the client paths of a drop to the redirected drives and checks that every item
/// can be read from the session, before the drag inside the session starts.
/// </summary>
internal static class ItemMapper
{
    private const int MaxNamesInMessage = 5;

    private const string NoRedirectionText =
        "The local drives are not redirected to this session, so the dropped files cannot be read. "
        + "In the connection settings, open Local Resources, choose More, select the drives to share, then reconnect.";

    /// <summary>
    /// Returns the mapped items, or null with <paramref name="error"/> set to a text for
    /// the user when an item cannot be dropped or reached.
    /// </summary>
    public static IReadOnlyList<MappedItem>? Map(IReadOnlyList<DropItem> items, DriveRedirectionProbe probe, out string? error)
    {
        var mapped = new List<MappedItem>(items.Count);
        var unreachable = new List<string>();
        var rejected = new List<string>();
        foreach (var item in items)
        {
            if (!ClientPathMapper.TryMapToTsClient(item.ClientPath, out var path, out var reason))
            {
                rejected.Add(reason ?? $"\"{item.ClientPath}\" cannot be dropped.");
                continue;
            }

            if (!Exists(path, item.IsDirectory))
            {
                unreachable.Add(item.ClientPath);
                continue;
            }

            mapped.Add(new MappedItem(path, item.IsDirectory, NameOf(path)));
        }

        if (rejected.Count > 0)
        {
            error = string.Join(" ", rejected);
            return null;
        }

        if (unreachable.Count > 0)
        {
            error = probe.IsAvailable() ? UnreachableText(unreachable) : NoRedirectionText;
            return null;
        }

        error = null;
        return mapped;
    }

    public static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException;

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
}
