namespace UltimateRDP.Core.Direct;

/// <summary>
/// Where the files of a direct transfer go. Unlike the storage of the channel, a file of
/// the direct connection is written by blocks at their offsets, from several threads, and
/// read back to check its SHA-256.
/// </summary>
public interface IDirectStorage
{
    /// <summary>Creates a folder, for a dropped folder that may hold no file.</summary>
    void CreateDirectory(string relativePath);

    /// <summary>Creates a new empty file, and its folders, for the blocks of <paramref name="size"/> bytes to come.</summary>
    IDirectFile CreateFile(string relativePath, long size);

    /// <summary>Called once a file is closed and checked; sets its last write time when known.</summary>
    void CompleteFile(string relativePath, DateTime? modified);

    /// <summary>Deletes everything written for the transfer, after a failure or a cancel.</summary>
    void DeleteAll();
}

/// <summary>
/// One file of a direct transfer, written and read at given offsets. <see cref="Write"/> and
/// <see cref="Read"/> may run on several threads at once, for different blocks.
/// </summary>
public interface IDirectFile : IDisposable
{
    /// <summary>Writes <paramref name="data"/> at <paramref name="offset"/>.</summary>
    void Write(long offset, ReadOnlySpan<byte> data);

    /// <summary>Reads up to the length of <paramref name="buffer"/> from <paramref name="offset"/>; returns the bytes read.</summary>
    int Read(long offset, Span<byte> buffer);
}
