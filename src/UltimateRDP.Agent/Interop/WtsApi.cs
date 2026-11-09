using System.Runtime.InteropServices;

namespace UltimateRDP.Agent.Interop;

/// <summary>
/// Remote Desktop Services API (wtsapi32.dll).
/// </summary>
internal static partial class WtsApi
{
    /// <summary>WTS_CURRENT_SERVER_HANDLE: the server the process runs on.</summary>
    public static readonly IntPtr CurrentServerHandle = IntPtr.Zero;

    /// <summary>WTS_CURRENT_SESSION: the session the process runs in.</summary>
    public const int CurrentSession = -1;

    /// <summary>NOTIFY_FOR_THIS_SESSION: only report changes of the calling session.</summary>
    public const int NotifyForThisSession = 0;

    /// <summary>
    /// Opens the server end of a static virtual channel. Returns IntPtr.Zero on failure,
    /// with the reason in GetLastError. The name is an ANSI string of at most seven
    /// characters.
    /// </summary>
    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSVirtualChannelOpen", SetLastError = true, StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(System.Runtime.InteropServices.Marshalling.AnsiStringMarshaller))]
    public static partial IntPtr WTSVirtualChannelOpen(IntPtr hServer, int sessionId, string pVirtualName);

    /// <summary>
    /// Reads one block of data sent by the client. Waits up to <paramref name="timeOut"/>
    /// milliseconds. Returns false on failure; a timeout is reported either as success
    /// with zero bytes or as a failure with WAIT_TIMEOUT or ERROR_IO_INCOMPLETE.
    /// </summary>
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool WTSVirtualChannelRead(IntPtr hChannelHandle, uint timeOut, byte* buffer, uint bufferSize, out uint bytesRead);

    /// <summary>
    /// Writes data to the client end of the channel. The system splits it into chunks.
    /// Returns false on failure.
    /// </summary>
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool WTSVirtualChannelWrite(IntPtr hChannelHandle, byte* buffer, uint length, out uint bytesWritten);

    /// <summary>WTSVirtualFileHandle of WTS_VIRTUAL_CLASS: the file handle of a channel.</summary>
    public const int VirtualFileHandle = 1;

    /// <summary>WTSClientAddress of WTS_INFO_CLASS: a WTS_CLIENT_ADDRESS.</summary>
    public const int ClientAddressInfo = 14;

    /// <summary>WTSSessionInfoEx of WTS_INFO_CLASS: a WTSINFOEX.</summary>
    public const int SessionInfoEx = 25;

    /// <summary>
    /// Gives information about a channel. With <see cref="VirtualFileHandle"/>, the buffer
    /// holds the file handle of the channel, which belongs to the channel: duplicate it to
    /// keep it. Free the buffer with <see cref="WTSFreeMemory"/>.
    /// </summary>
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSVirtualChannelQuery(IntPtr hChannelHandle, int virtualClass, out IntPtr buffer, out uint bytesReturned);

    /// <summary>Gives information about a session. Free the buffer with <see cref="WTSFreeMemory"/>.</summary>
    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

    /// <summary>Frees a buffer given by the WTS functions.</summary>
    [LibraryImport("wtsapi32.dll")]
    public static partial void WTSFreeMemory(IntPtr memory);

    /// <summary>Closes a handle returned by WTSVirtualChannelOpen.</summary>
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSVirtualChannelClose(IntPtr hChannelHandle);

    /// <summary>
    /// Asks Windows to post WM_WTSSESSION_CHANGE to <paramref name="hWnd"/> when the
    /// session connects, disconnects, locks and so on.
    /// </summary>
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

    /// <summary>Stops the notifications requested with WTSRegisterSessionNotification.</summary>
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSUnRegisterSessionNotification(IntPtr hWnd);
}
