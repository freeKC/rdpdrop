namespace UltimateRDP.Core.Tests.Direct;

/// <summary>
/// Two connected in memory streams, as the two ends of a TCP connection: what one end
/// writes, the other reads, with a bounded buffer per direction so that a writer waits for
/// its reader as with TCP. Disposing an end closes it: the other end reads the end of the
/// stream once the data written before is read, and its writes fail as after a reset.
/// </summary>
internal static class DuplexPipe
{
    /// <summary>A new connection. <paramref name="cutClientToAgentAfter"/> ends the direction from the client after that many bytes, as a broken connection.</summary>
    public static (Stream Client, Stream Agent) Create(long cutClientToAgentAfter = long.MaxValue, int capacity = 4 * 1024 * 1024)
    {
        var toAgent = new Direction(capacity, cutClientToAgentAfter);
        var toClient = new Direction(capacity, long.MaxValue);
        return (new End(toClient, toAgent), new End(toAgent, toClient));
    }

    /// <summary>One direction of the connection.</summary>
    private sealed class Direction(int capacity, long cutAfter)
    {
        private readonly Queue<byte[]> _segments = new();
        private readonly object _gate = new();
        private int _offset;
        private long _buffered;
        private long _written;
        private bool _completed;
        private bool _abandoned;

        public void Write(ReadOnlySpan<byte> data)
        {
            lock (_gate)
            {
                while (!data.IsEmpty)
                {
                    if (_abandoned || _completed)
                    {
                        throw new IOException("The connection was reset by the other end.");
                    }

                    if (_written >= cutAfter)
                    {
                        _completed = true;
                        Monitor.PulseAll(_gate);
                        throw new IOException("The connection was cut.");
                    }

                    while (_buffered >= capacity && !_abandoned)
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_abandoned)
                    {
                        throw new IOException("The connection was reset by the other end.");
                    }

                    var count = (int)Math.Min(data.Length, Math.Min(capacity - _buffered, cutAfter - _written));
                    _segments.Enqueue(data[..count].ToArray());
                    _buffered += count;
                    _written += count;
                    data = data[count..];
                    Monitor.PulseAll(_gate);
                }
            }
        }

        public int Read(Span<byte> buffer)
        {
            lock (_gate)
            {
                while (_segments.Count == 0)
                {
                    if (_completed || _written >= cutAfter)
                    {
                        return 0;
                    }

                    if (_abandoned)
                    {
                        throw new ObjectDisposedException("The stream was closed.");
                    }

                    Monitor.Wait(_gate);
                }

                var segment = _segments.Peek();
                var count = Math.Min(buffer.Length, segment.Length - _offset);
                segment.AsSpan(_offset, count).CopyTo(buffer);
                _offset += count;
                if (_offset == segment.Length)
                {
                    _segments.Dequeue();
                    _offset = 0;
                }

                _buffered -= count;
                Monitor.PulseAll(_gate);
                return count;
            }
        }

        /// <summary>The writer closed: the reader gets the end of the stream after the data.</summary>
        public void Complete()
        {
            lock (_gate)
            {
                _completed = true;
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>The reader closed: the writer gets an error.</summary>
        public void Abandon()
        {
            lock (_gate)
            {
                _abandoned = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private sealed class End(Direction incoming, Direction outgoing) : Stream
    {
        private int _disposed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return buffer.IsEmpty ? 0 : incoming.Read(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            outgoing.Write(buffer);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                outgoing.Complete();
                incoming.Abandon();
            }

            base.Dispose(disposing);
        }
    }
}
