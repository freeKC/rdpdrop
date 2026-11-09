using System.Runtime.InteropServices;

namespace UltimateRDP.Agent.Interop;

/// <summary>
/// Window functions (user32.dll).
/// </summary>
internal static partial class User32
{
    /// <summary>GA_ROOT: the top level window that contains the given window.</summary>
    public const uint GaRoot = 2;

    /// <summary>SM_REMOTESESSION: nonzero when the process runs in a remote session.</summary>
    public const int SmRemoteSession = 0x1000;

    /// <summary>Window message posted after WTSRegisterSessionNotification.</summary>
    public const int WmWtsSessionChange = 0x02B1;

    /// <summary>wParam of WM_WTSSESSION_CHANGE: the session was connected to the console.</summary>
    public const int WtsConsoleConnect = 0x1;

    /// <summary>wParam of WM_WTSSESSION_CHANGE: the session was disconnected from the console.</summary>
    public const int WtsConsoleDisconnect = 0x2;

    /// <summary>wParam of WM_WTSSESSION_CHANGE: a remote client connected to the session.</summary>
    public const int WtsRemoteConnect = 0x3;

    /// <summary>wParam of WM_WTSSESSION_CHANGE: the remote client disconnected.</summary>
    public const int WtsRemoteDisconnect = 0x4;

    /// <summary>wParam of WM_WTSSESSION_CHANGE: a user logged on to the session.</summary>
    public const int WtsSessionLogon = 0x5;

    /// <summary>WM_QUERYENDSESSION: the session is about to end; the answer must be TRUE at once.</summary>
    public const int WmQueryEndSession = 0x0011;

    /// <summary>WM_ENDSESSION: wParam is nonzero when the session really ends.</summary>
    public const int WmEndSession = 0x0016;

    /// <summary>WM_APP: first message number free for the application.</summary>
    public const int WmApp = 0x8000;

    /// <summary>MSG: a message taken from the queue of the thread.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    /// <summary>
    /// Waits for the next message of the thread. Returns a positive value for a message,
    /// zero for WM_QUIT and -1 on error.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    public static partial int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    /// <summary>Turns key messages into character messages.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in Msg lpMsg);

    /// <summary>Hands a message to the window procedure of its window.</summary>
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial IntPtr DispatchMessage(in Msg lpMsg);

    /// <summary>Screen point in physical pixels.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    /// <summary>Returns the window at a screen point, or IntPtr.Zero.</summary>
    [LibraryImport("user32.dll")]
    public static partial IntPtr WindowFromPoint(Point point);

    /// <summary>Returns an ancestor of a window; with GA_ROOT, its top level window.</summary>
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    /// <summary>
    /// Copies the window class name into the buffer. Returns the number of characters
    /// copied, or zero on failure.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
    public static unsafe partial int GetClassName(IntPtr hWnd, char* lpClassName, int nMaxCount);

    /// <summary>Returns a system metric such as SM_REMOTESESSION.</summary>
    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    /// <summary>Destroys an icon created by Bitmap.GetHicon.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);

    /// <summary>Returns the class name of a window, or an empty string.</summary>
    public static unsafe string GetClassName(IntPtr hWnd)
    {
        // Class names are limited to 256 characters.
        const int Capacity = 257;
        var buffer = stackalloc char[Capacity];
        var length = GetClassName(hWnd, buffer, Capacity);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}
