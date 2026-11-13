using System.Collections.Concurrent;
using UltimateRDP.Core.Direct;

namespace UltimateRDP.Core.Tests.Direct;

/// <summary>Keeps the files of a direct transfer in memory; thread safe, as the receiver writes from several threads.</summary>
internal sealed class MemoryDirectStorage : IDirectStorage
{
    private readonly Lock _gate = new();
    private int _deleteAllCalls;

    public ConcurrentDictionary<string, MemoryDirectFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<string> Directories { get; } = new();

    public ConcurrentDictionary<string, DateTime?> Completed { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int DeleteAllCalls => Volatile.Read(ref _deleteAllCalls);

    public bool FailOnCreate { get; set; }

    public void CreateDirectory(string relativePath) => Directories.Enqueue(relativePath);

    public IDirectFile CreateFile(string relativePath, long size)
    {
        if (FailOnCreate)
        {
            throw new IOException("The disk is full.");
        }

        var file = new MemoryDirectFile(size);
        lock (_gate)
        {
            if (!Files.TryAdd(relativePath, file))
            {
                throw new IOException($"{relativePath} exists.");
            }
        }

        return file;
    }

    public void CompleteFile(string relativePath, DateTime? modified) => Completed[relativePath] = modified;

    public void DeleteAll()
    {
        Interlocked.Increment(ref _deleteAllCalls);
        Files.Clear();
        Completed.Clear();
    }

    public byte[] Content(string relativePath) => Files[relativePath].ToArray();
}

/// <summary>One file of <see cref="MemoryDirectStorage"/>.</summary>
internal sealed class MemoryDirectFile(long size) : IDirectFile
{
    private readonly Lock _gate = new();
    private readonly byte[] _data = new byte[size];
    private long _length;

    public bool Disposed { get; private set; }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            data.CopyTo(_data.AsSpan((int)offset));
            _length = Math.Max(_length, offset + data.Length);
        }
    }

    public int Read(long offset, Span<byte> buffer)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            var count = (int)Math.Max(0, Math.Min(buffer.Length, _length - offset));
            _data.AsSpan((int)offset, count).CopyTo(buffer);
            return count;
        }
    }

    public byte[] ToArray()
    {
        lock (_gate)
        {
            return _data[..(int)_length];
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Disposed = true;
        }
    }
}

/// <summary>
/// Holds each block only until the verifier reads it back, which it does once: a large
/// transfer goes through without its bytes in memory.
/// </summary>
internal sealed class StreamingDirectStorage : IDirectStorage
{
    private long _written;

    public long Written => Interlocked.Read(ref _written);

    public void CreateDirectory(string relativePath)
    {
    }

    public IDirectFile CreateFile(string relativePath, long size) => new File(this);

    public void CompleteFile(string relativePath, DateTime? modified)
    {
    }

    public void DeleteAll()
    {
    }

    private sealed class File(StreamingDirectStorage owner) : IDirectFile
    {
        private readonly ConcurrentDictionary<long, byte[]> _blocks = new();

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            _blocks[offset] = data.ToArray();
            Interlocked.Add(ref owner._written, data.Length);
        }

        public int Read(long offset, Span<byte> buffer)
        {
            if (!_blocks.TryRemove(offset, out var block))
            {
                return 0;
            }

            block.CopyTo(buffer);
            return block.Length;
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>A stream of <paramref name="length"/> bytes that repeats one block.</summary>
internal sealed class RepeatingStream(byte[] block, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length && _position < length)
        {
            var at = (int)(_position % block.Length);
            var count = (int)Math.Min(Math.Min(buffer.Length - total, block.Length - at), length - _position);
            block.AsSpan(at, count).CopyTo(buffer[total..]);
            total += count;
            _position += count;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A file whose first read blocks until <see cref="Release"/>, as when an antivirus scans it.</summary>
internal sealed class BlockingStream(byte[] data) : Stream
{
    private readonly MemoryStream _inner = new(data, writable: false);
    private readonly ManualResetEventSlim _released = new(false);

    public ManualResetEventSlim Entered { get; } = new(false);

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public void Release() => _released.Set();

    public override int Read(Span<byte> buffer)
    {
        Entered.Set();
        _released.Wait();
        return _inner.Read(buffer);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>A storage in memory whose deletion takes a while, as a large partial file may on a server.</summary>
internal sealed class SlowDeleteStorage(TimeSpan delay) : IDirectStorage
{
    public MemoryDirectStorage Inner { get; } = new();

    public void CreateDirectory(string relativePath) => Inner.CreateDirectory(relativePath);

    public IDirectFile CreateFile(string relativePath, long size) => Inner.CreateFile(relativePath, size);

    public void CompleteFile(string relativePath, DateTime? modified) => Inner.CompleteFile(relativePath, modified);

    public void DeleteAll()
    {
        Thread.Sleep(delay);
        Inner.DeleteAll();
    }
}
