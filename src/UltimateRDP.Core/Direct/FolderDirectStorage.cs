using Microsoft.Win32.SafeHandles;

namespace UltimateRDP.Core.Direct;

/// <summary>
/// The files of a direct transfer in a folder of their own, since 0.9.0 the hidden part folder
/// of the client for the files that the session sends. Every full path is checked to stay in
/// the folder, besides the checks of the relative paths by <see cref="DirectReceiver"/>. A
/// file is created empty when the list is checked, opened at its first block and closed once
/// it was checked, so that a folder of many small files does not hold a handle per file.
/// </summary>
public sealed class FolderDirectStorage : IDirectStorage
{
    private readonly string _folder;
    private readonly string _prefix;
    private readonly Action<string>? _completed;
    private readonly Action<string>? _deleteFailed;

    /// <param name="folder">The folder of the transfer; it is created when missing.</param>
    /// <param name="completed">Called with the full path of each file once it was checked and its time set; an exception of it is not caught.</param>
    /// <param name="deleteFailed">Called with the reason when the folder cannot be deleted after a failure.</param>
    public FolderDirectStorage(string folder, Action<string>? completed = null, Action<string>? deleteFailed = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        _folder = Path.GetFullPath(folder);
        _prefix = _folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _completed = completed;
        _deleteFailed = deleteFailed;
        Directory.CreateDirectory(_folder);
    }

    /// <summary>The folder of the transfer.</summary>
    public string Folder => _folder;

    /// <inheritdoc />
    public void CreateDirectory(string relativePath) => Directory.CreateDirectory(FullPath(relativePath));

    /// <inheritdoc />
    public IDirectFile CreateFile(string relativePath, long size)
    {
        var path = FullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read).Dispose();
        return new PositionalFile(path);
    }

    /// <inheritdoc />
    public void CompleteFile(string relativePath, DateTime? modified)
    {
        var path = FullPath(relativePath);
        if (modified is { } time)
        {
            File.SetLastWriteTimeUtc(path, DateTime.SpecifyKind(time, DateTimeKind.Utc));
        }

        _completed?.Invoke(path);
    }

    /// <inheritdoc />
    public void DeleteAll()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _deleteFailed?.Invoke(ex.Message);
        }
    }

    /// <summary>The full path of a relative path; an <see cref="IOException"/> when it leaves the folder.</summary>
    public string FullPath(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(_folder, relativePath));
        if (!path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"The path {relativePath} leaves the folder of the transfer.");
        }

        return path;
    }

    /// <summary>One file: positional reads and writes on one handle, opened at the first use, closed at the end.</summary>
    private sealed class PositionalFile(string path) : IDirectFile
    {
        private readonly Lock _gate = new();
        private SafeFileHandle? _handle;
        private bool _disposed;

        public void Write(long offset, ReadOnlySpan<byte> data) => RandomAccess.Write(Handle(), data, offset);

        public int Read(long offset, Span<byte> buffer) => RandomAccess.Read(Handle(), buffer, offset);

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _handle?.Dispose();
                _handle = null;
            }
        }

        private SafeFileHandle Handle()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _handle ??= File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            }
        }
    }
}
