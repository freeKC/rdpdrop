using System.Runtime.InteropServices;
using UltimateRDP.Agent.Interop;

namespace UltimateRDP.Agent.Channel;

/// <summary>
/// Reads a static virtual channel through its file handle with ReadFile and a large
/// buffer, instead of WTSVirtualChannelRead: the <c>file</c> value of the
/// <c>channelRead</c> setting, an experiment to see whether one read can return more than
/// one chunk. The handle of the channel is duplicated, so that this reader closes its own.
/// </summary>
/// <remarks>
/// <para>
/// The handle is opened for overlapped operations, so every read is an overlapped one:
/// <see cref="Read"/> starts a read, waits up to its timeout, and leaves the read pending
/// when no data came; the next call waits for the same read. The owner thus checks its
/// stop and restart flags as often as with WTSVirtualChannelRead. <see cref="Dispose"/>
/// cancels a pending read and waits for it to end before it frees the buffer and the
/// OVERLAPPED structure.
/// </para>
/// <para>The data may hold a CHANNEL_PDU_HEADER before each chunk; the owner handles it.</para>
/// <para>One thread only: the thread that owns the channel.</para>
/// </remarks>
internal sealed unsafe class ChannelFileReader : IDisposable
{
    /// <summary>Size of one read.</summary>
    public const int BufferSize = 64 * 1024;

    private readonly IntPtr _file;
    private readonly ManualResetEvent _done = new(false);
    private readonly byte* _buffer;
    private readonly NativeOverlapped* _overlapped;
    private bool _pending;
    private bool _disposed;

    private ChannelFileReader(IntPtr file)
    {
        _file = file;
        _buffer = (byte*)NativeMemory.Alloc(BufferSize);
        _overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        _overlapped->EventHandle = _done.SafeWaitHandle.DangerousGetHandle();
    }

    /// <summary>
    /// Duplicates the file handle of the channel <paramref name="channelHandle"/>. Returns
    /// null and says why when it cannot be obtained.
    /// </summary>
    public static ChannelFileReader? TryOpen(IntPtr channelHandle, out string? error)
    {
        if (!WtsApi.WTSVirtualChannelQuery(channelHandle, WtsApi.VirtualFileHandle, out var buffer, out var size))
        {
            error = $"WTSVirtualChannelQuery failed with error {Marshal.GetLastPInvokeError()}";
            return null;
        }

        try
        {
            if (buffer == IntPtr.Zero || size < (uint)IntPtr.Size)
            {
                error = $"WTSVirtualChannelQuery gave {size} bytes instead of a handle";
                return null;
            }

            var source = Marshal.ReadIntPtr(buffer);
            var process = Kernel32.GetCurrentProcess();
            if (!Kernel32.DuplicateHandle(process, source, process, out var copy, 0, false, Kernel32.DuplicateSameAccess))
            {
                error = $"DuplicateHandle failed with error {Marshal.GetLastPInvokeError()}";
                return null;
            }

            error = null;
            return new ChannelFileReader(copy);
        }
        finally
        {
            WtsApi.WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMilliseconds"/> for data and copies it to
    /// <paramref name="destination"/>, at least <see cref="BufferSize"/> long. Returns the
    /// number of bytes, zero when nothing came in time.
    /// </summary>
    /// <exception cref="ChannelException">The read failed.</exception>
    public int Read(byte[] destination, int timeoutMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Length < BufferSize)
        {
            throw new ArgumentException("The destination is shorter than one read.", nameof(destination));
        }

        if (!_pending)
        {
            _done.Reset();
            uint immediate;
            if (Kernel32.ReadFile(_file, _buffer, BufferSize, &immediate, _overlapped))
            {
                // Done at once; the result is also in the OVERLAPPED structure.
                return Complete(immediate, destination);
            }

            var error = Marshal.GetLastPInvokeError();
            if (error != Kernel32.ErrorIoPending)
            {
                throw new ChannelException("ReadFile on the channel failed", error);
            }

            _pending = true;
        }

        if (!_done.WaitOne(timeoutMilliseconds))
        {
            return 0;
        }

        if (!Kernel32.GetOverlappedResult(_file, _overlapped, out var count, false))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == Kernel32.ErrorIoIncomplete)
            {
                return 0;
            }

            _pending = false;
            throw new ChannelException("ReadFile on the channel failed", error);
        }

        _pending = false;
        return Complete(count, destination);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pending)
        {
            // The system writes to the buffer and the OVERLAPPED structure until the read
            // ends: it must end before they are freed.
            Kernel32.CancelIoEx(_file, _overlapped);
            Kernel32.GetOverlappedResult(_file, _overlapped, out _, true);
            _pending = false;
        }

        Kernel32.CloseHandle(_file);
        NativeMemory.Free(_overlapped);
        NativeMemory.Free(_buffer);
        _done.Dispose();
    }

    private int Complete(uint count, byte[] destination)
    {
        var length = (int)Math.Min(count, (uint)BufferSize);
        new ReadOnlySpan<byte>(_buffer, length).CopyTo(destination);
        return length;
    }
}
