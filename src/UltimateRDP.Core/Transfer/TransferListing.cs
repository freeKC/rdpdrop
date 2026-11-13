using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Transfer;

/// <summary>
/// The files of the dropped items, in the order they are sent, with their path on this
/// computer and their path in the transfer: folders file by file, hidden and system files
/// included, junctions and symbolic links to folders skipped. Shared by the transfer through
/// the channel and the transfer through the direct connection, on the client for a drop and
/// on the agent for the files sent to the client since 0.9.0.
/// </summary>
public static class TransferListing
{
    /// <summary>Every file, hidden and system ones too, as a copy by Explorer would take them.</summary>
    public static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Lists the files of <paramref name="items"/>. Throws a <see cref="TransferListingException"/>
    /// that names the item when a file or folder cannot be read.
    /// </summary>
    public static List<(string Path, TransferFile File)> List(IReadOnlyList<DropItem> items)
    {
        var files = new List<(string Path, TransferFile File)>();
        foreach (var item in items)
        {
            var name = TransferPaths.ItemName(item.ClientPath);
            try
            {
                if (!item.IsDirectory)
                {
                    var info = new FileInfo(item.ClientPath);
                    files.Add((info.FullName, new TransferFile(name, info.Length, info.LastWriteTimeUtc)));
                    continue;
                }

                var root = new DirectoryInfo(item.ClientPath);
                var pending = new Stack<DirectoryInfo>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    var folder = pending.Pop();
                    foreach (var entry in folder.EnumerateFileSystemInfos("*", Options))
                    {
                        if (entry is FileInfo info)
                        {
                            var relative = TransferPaths.Combine(name, Path.GetRelativePath(root.FullName, info.FullName));
                            files.Add((info.FullName, new TransferFile(relative, info.Length, info.LastWriteTimeUtc)));
                        }
                        else if (entry is DirectoryInfo child && !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            // A junction or a link to a folder could loop, or leave the dropped folder.
                            pending.Push(child);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new TransferListingException(item.ClientPath, ex);
            }
        }

        return files;
    }
}

/// <summary>A dropped item that cannot be listed.</summary>
public sealed class TransferListingException(string path, Exception inner) : IOException(inner.Message, inner)
{
    /// <summary>The path of the dropped item on this computer.</summary>
    public string ItemPath { get; } = path;
}
