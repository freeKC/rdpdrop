using System.Security;
using UltimateRDP.Agent.Localization;
using UltimateRDP.Agent.Resources;
using UltimateRDP.Core.Paths;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Agent.Drops;

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

    /// <summary>
    /// Returns the mapped items, or null with <paramref name="error"/> set to a text for
    /// the user when an item cannot be dropped or reached.
    /// </summary>
    public static IReadOnlyList<MappedItem>? Map(IReadOnlyList<DropItem> items, DriveRedirectionProbe probe, out LocalText? error)
    {
        var mapped = new List<MappedItem>(items.Count);
        var unreachable = new List<string>();
        var rejected = new List<LocalText>();
        foreach (var item in items)
        {
            var rejection = ClientPathMapper.Classify(item.ClientPath, out var path);
            if (rejection != PathRejection.None)
            {
                rejected.Add(PathText(rejection, item.ClientPath));
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
            error = LocalText.Join(rejected);
            return null;
        }

        if (unreachable.Count > 0)
        {
            error = probe.IsAvailable() ? UnreachableText(unreachable) : LocalText.Of(nameof(Strings.NoRedirection));
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

    private static LocalText UnreachableText(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1)
        {
            return LocalText.Of(nameof(Strings.UnreachableOne), paths[0]);
        }

        var names = string.Join(", ", paths.Take(MaxNamesInMessage));
        object list = paths.Count > MaxNamesInMessage
            ? LocalText.Of(nameof(Strings.UnreachableMore), names, paths.Count - MaxNamesInMessage)
            : names;
        return LocalText.Of(nameof(Strings.UnreachableMany), paths.Count, list);
    }

    /// <summary>Why <paramref name="path"/> cannot be dropped, in words for the user.</summary>
    private static LocalText PathText(PathRejection rejection, string path) => rejection switch
    {
        PathRejection.Empty => LocalText.Of(nameof(Strings.PathEmpty)),
        PathRejection.DevicePath => LocalText.Of(nameof(Strings.PathDevice), path),
        PathRejection.NetworkPath => LocalText.Of(nameof(Strings.PathNetwork), path),
        PathRejection.RelativePath => LocalText.Of(nameof(Strings.PathRelative), path),
        PathRejection.DriveRelativePath => LocalText.Of(nameof(Strings.PathDriveRelative), path),
        PathRejection.DotSegment => LocalText.Of(nameof(Strings.PathDotSegment), path),
        _ => LocalText.Of(nameof(Strings.PathInvalidCharacters), path),
    };

}
