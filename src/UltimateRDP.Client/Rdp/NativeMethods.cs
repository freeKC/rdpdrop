using System.Runtime.InteropServices;
using System.Text;

namespace UltimateRDP.Client.Rdp;

/// <summary>
/// Win32 and OLE declarations used to register drop targets on the windows that the
/// ActiveX control creates.
/// </summary>
internal static class NativeMethods
{
    public const string OleDropTargetProperty = "OleDropTargetInterface";

    /// <summary>GA_ROOT of GetAncestor: the top level window.</summary>
    public const uint GaRoot = 2;

    /// <summary>GW_OWNER of GetWindow.</summary>
    public const uint GwOwner = 4;

    /// <summary>GWL_STYLE of GetWindowLongPtr.</summary>
    public const int GwlStyle = -16;

    /// <summary>GWL_EXSTYLE of GetWindowLongPtr.</summary>
    public const int GwlExStyle = -20;

    /// <summary>EVENT_OBJECT_CREATE of SetWinEventHook.</summary>
    public const uint EventObjectCreate = 0x8000;

    /// <summary>EVENT_OBJECT_DESTROY of SetWinEventHook.</summary>
    public const uint EventObjectDestroy = 0x8001;

    /// <summary>EVENT_OBJECT_SHOW of SetWinEventHook.</summary>
    public const uint EventObjectShow = 0x8002;

    /// <summary>WINEVENT_OUTOFCONTEXT: the callback runs in this process, on the thread that set the hook, through its messages.</summary>
    public const uint WinEventOutOfContext = 0;

    /// <summary>OBJID_WINDOW: the event is about the window itself.</summary>
    public const int ObjIdWindow = 0;

    /// <summary>QS_ALLINPUT of MsgWaitForMultipleObjectsEx.</summary>
    public const uint QsAllInput = 0x04FF;

    /// <summary>MWMO_INPUTAVAILABLE of MsgWaitForMultipleObjectsEx.</summary>
    public const uint MwmoInputAvailable = 0x0004;

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>HWND_TOPMOST of SetWindowPos.</summary>
    public static readonly IntPtr HwndTopMost = new(-1);

    /// <summary>HWND_NOTOPMOST of SetWindowPos.</summary>
    public static readonly IntPtr HwndNoTopMost = new(-2);

    /// <summary>SWP_NOSIZE of SetWindowPos.</summary>
    public const uint SwpNoSize = 0x0001;

    /// <summary>SWP_NOMOVE of SetWindowPos.</summary>
    public const uint SwpNoMove = 0x0002;

    /// <summary>SWP_NOACTIVATE of SetWindowPos.</summary>
    public const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetProp(IntPtr hwnd, string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint milliseconds, uint wakeMask, uint flags);

    [DllImport("ole32.dll")]
    public static extern int RegisterDragDrop(IntPtr hwnd, IOleDropTarget dropTarget);

    [DllImport("ole32.dll")]
    public static extern int RevokeDragDrop(IntPtr hwnd);

    public static string ClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "?";
    }

    public static bool HasDropTarget(IntPtr hwnd) => GetProp(hwnd, OleDropTargetProperty) != IntPtr.Zero;

    /// <summary>The rectangle of the window on the screen, or an empty one when it cannot be read.</summary>
    public static Rectangle WindowRectangle(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var rect) ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) : Rectangle.Empty;

    /// <summary>The top level window under the screen point, or IntPtr.Zero.</summary>
    public static IntPtr TopLevelWindowAt(Point screenPoint)
    {
        var hwnd = WindowFromPoint(screenPoint);
        return hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GaRoot);
    }

    public static List<IntPtr> ChildWindows(IntPtr parent)
    {
        var result = new List<IntPtr>();
        EnumChildWindows(parent, (hwnd, _) =>
        {
            result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>The top level windows of this process, visible or not.</summary>
    public static List<IntPtr> TopLevelWindowsOfThisProcess()
    {
        var processId = (uint)Environment.ProcessId;
        var result = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == processId)
            {
                result.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>SW_HIDE.</summary>
    public const int SwHide = 0;

    /// <summary>SW_SHOWNOACTIVATE.</summary>
    public const int SwShowNoActivate = 4;

    /// <summary>SW_SHOW.</summary>
    public const int SwShow = 5;

    /// <summary>WS_EX_TOOLWINDOW: no taskbar button and no place in Alt+Tab.</summary>
    public const long ExToolWindow = 0x00000080;

    /// <summary>WS_EX_APPWINDOW: a taskbar button.</summary>
    public const long ExAppWindow = 0x00040000;

    /// <summary>DWMWA_CLOAKED.</summary>
    private const int DwmwaCloaked = 14;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>The title of a window, at most 256 characters.</summary>
    public static string WindowText(IntPtr hwnd)
    {
        var text = new StringBuilder(257);
        return GetWindowText(hwnd, text, text.Capacity) > 0 ? text.ToString() : string.Empty;
    }

    /// <summary>True when the window is cloaked by DWM, so that it shows nothing although Windows calls it visible.</summary>
    public static bool IsCloaked(IntPtr hwnd) => DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>Adds and removes bits of the extended style of a window.</summary>
    public static void ChangeExStyle(IntPtr hwnd, long add, long remove)
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr((style | add) & ~remove));
    }
}
