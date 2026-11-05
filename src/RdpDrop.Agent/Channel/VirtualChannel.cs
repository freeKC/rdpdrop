using System.ComponentModel;
using System.Runtime.InteropServices;
using RdpDrop.Agent.Interop;

namespace RdpDrop.Agent.Channel;

/// <summary>
/// Server end of the static virtual channel. One instance per connection: once a read
/// or a write fails, dispose it and open a new one.
/// </summary>
/// <remarks>
/// Reads use WTSVirtualChannelRead with a short timeout so that the owner can stop or
/// restart the loop without closing a handle under a pending read. Reads deliver the
/// data in blocks of at most about 1600 bytes; the caller reassembles lines.
/// Writes may come from any thread. Reads and <see cref="Dispose"/> must come from
/// the thread that owns the instance.
/// </remarks>
internal sealed unsafe class VirtualChannel : IDisposable
{
    private const int ErrorIoIncomplete = 996;
    private const int WaitTimeout = 258;

    private readonly object _gate = new();
    private IntPtr _handle;

    private VirtualChannel(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Opens the channel in the current session. Returns null and the Win32 error code
    /// when the channel cannot be opened.
    /// </summary>
    public static VirtualChannel? TryOpen(string name, out int error)
    {
        var handle = WtsApi.WTSVirtualChannelOpen(WtsApi.CurrentServerHandle, WtsApi.CurrentSession, name);
        if (handle == IntPtr.Zero)
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }

        error = 0;
        return new VirtualChannel(handle);
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMilliseconds"/> for data. Returns the number of
    /// bytes read, zero on timeout.
    /// </summary>
    /// <exception cref="ChannelException">The channel is broken.</exception>
    public int Read(byte[] buffer, int timeoutMilliseconds)
    {
        var handle = _handle;
        if (handle == IntPtr.Zero)
        {
            throw new ChannelException("The channel is closed", 0);
        }

        bool ok;
        uint read;
        fixed (byte* pointer = buffer)
        {
            ok = WtsApi.WTSVirtualChannelRead(handle, (uint)timeoutMilliseconds, pointer, (uint)buffer.Length, out read);
        }

        if (ok)
        {
            return (int)read;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error is ErrorIoIncomplete or WaitTimeout)
        {
            return 0;
        }

        throw new ChannelException("Reading from the channel failed", error);
    }

    /// <summary>Writes every byte of <paramref name="data"/>.</summary>
    /// <exception cref="ChannelException">The channel is broken or closed.</exception>
    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero)
            {
                throw new ChannelException("The channel is closed", 0);
            }

            while (!data.IsEmpty)
            {
                bool ok;
                uint written;
                fixed (byte* pointer = data)
                {
                    ok = WtsApi.WTSVirtualChannelWrite(_handle, pointer, (uint)data.Length, out written);
                }

                if (!ok)
                {
                    throw new ChannelException("Writing to the channel failed", Marshal.GetLastPInvokeError());
                }

                if (written == 0)
                {
                    throw new ChannelException("The channel accepted no data", 0);
                }

                data = data[(int)Math.Min(written, (uint)data.Length)..];
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle != IntPtr.Zero)
            {
                WtsApi.WTSVirtualChannelClose(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}

/// <summary>
/// A virtual channel call failed. <see cref="Win32Error"/> holds the Win32 error, or zero.
/// </summary>
internal sealed class ChannelException : IOException
{
    public ChannelException(string message, int errorCode)
        : base(errorCode == 0 ? message : $"{message} (error {errorCode}: {new Win32Exception(errorCode).Message})")
    {
        Win32Error = errorCode;
    }

    public int Win32Error { get; }
}
