using System.Runtime.InteropServices;

namespace RdpDrop.Agent.Interop;

/// <summary>
/// Input, cursor and window functions (user32.dll) used by the drag source.
/// </summary>
internal static partial class User32
{
    /// <summary>INPUT_MOUSE: the INPUT structure holds a MOUSEINPUT.</summary>
    public const int InputMouse = 0;

    /// <summary>MOUSEEVENTF_MOVE: the event moves the cursor.</summary>
    public const uint MouseEventMove = 0x0001;

    /// <summary>MOUSEEVENTF_LEFTDOWN: the physical left button goes down.</summary>
    public const uint MouseEventLeftDown = 0x0002;

    /// <summary>MOUSEEVENTF_LEFTUP: the physical left button goes up.</summary>
    public const uint MouseEventLeftUp = 0x0004;

    /// <summary>MOUSEEVENTF_RIGHTDOWN: the physical right button goes down.</summary>
    public const uint MouseEventRightDown = 0x0008;

    /// <summary>MOUSEEVENTF_RIGHTUP: the physical right button goes up.</summary>
    public const uint MouseEventRightUp = 0x0010;

    /// <summary>MOUSEEVENTF_VIRTUALDESK: absolute coordinates cover the whole virtual screen.</summary>
    public const uint MouseEventVirtualDesk = 0x4000;

    /// <summary>MOUSEEVENTF_ABSOLUTE: dx and dy are normalised coordinates from 0 to 65535.</summary>
    public const uint MouseEventAbsolute = 0x8000;

    /// <summary>SM_SWAPBUTTON: nonzero when the left and right buttons are swapped.</summary>
    public const int SmSwapButton = 23;

    /// <summary>SM_XVIRTUALSCREEN: left edge of the virtual screen.</summary>
    public const int SmXVirtualScreen = 76;

    /// <summary>SM_YVIRTUALSCREEN: top edge of the virtual screen.</summary>
    public const int SmYVirtualScreen = 77;

    /// <summary>SM_CXVIRTUALSCREEN: width of the virtual screen.</summary>
    public const int SmCxVirtualScreen = 78;

    /// <summary>SM_CYVIRTUALSCREEN: height of the virtual screen.</summary>
    public const int SmCyVirtualScreen = 79;

    /// <summary>GA_ROOTOWNER: the owner chain of the top level window, up to its root.</summary>
    public const uint GaRootOwner = 3;

    /// <summary>VK_LBUTTON: virtual key of the logical left button.</summary>
    public const int VkLButton = 0x01;

    /// <summary>VK_RBUTTON: virtual key of the logical right button.</summary>
    public const int VkRButton = 0x02;

    /// <summary>VK_ESCAPE: virtual key of Escape.</summary>
    public const int VkEscape = 0x1B;

    /// <summary>WM_KEYDOWN: a key went down. Posted to the proxy window only, to end a drag.</summary>
    public const uint WmKeyDown = 0x0100;

    /// <summary>WM_MOUSEACTIVATE: asks a window whether a click activates it.</summary>
    public const int WmMouseActivate = 0x0021;

    /// <summary>MA_NOACTIVATE: answer to WM_MOUSEACTIVATE that keeps the window inactive.</summary>
    public const int MaNoActivate = 3;

    /// <summary>WM_DPICHANGED: the window moved to a monitor with another DPI.</summary>
    public const int WmDpiChanged = 0x02E0;

    /// <summary>WS_POPUP: a window without caption or border.</summary>
    public const int WsPopup = unchecked((int)0x80000000);

    /// <summary>WS_EX_TOPMOST: the window stays above the other windows.</summary>
    public const int WsExTopmost = 0x00000008;

    /// <summary>WS_EX_TOOLWINDOW: no taskbar button and no Alt+Tab entry.</summary>
    public const int WsExToolWindow = 0x00000080;

    /// <summary>WS_EX_LAYERED: the window has a transparency level.</summary>
    public const int WsExLayered = 0x00080000;

    /// <summary>WS_EX_NOACTIVATE: a click does not bring the window to the foreground.</summary>
    public const int WsExNoActivate = 0x08000000;

    /// <summary>LWA_ALPHA: SetLayeredWindowAttributes sets the opacity of the window.</summary>
    public const uint LwaAlpha = 0x2;

    /// <summary>MOUSEINPUT: one simulated mouse event.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>
    /// INPUT with its union. The union is as large as its largest member, KEYBDINPUT and
    /// HARDWAREINPUT being smaller than MOUSEINPUT, so a mouse only layout has the right size.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public int Type;
        public MouseInput Mouse;
    }

    /// <summary>
    /// Inserts input events in the input stream of the session, in order and without
    /// other input in between. Returns the number of events inserted; zero when the input
    /// was blocked, for example on a locked desktop.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static unsafe partial uint SendInput(uint cInputs, Input* pInputs, int cbSize);

    /// <summary>Reads the cursor position in physical pixels.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point point);

    /// <summary>Moves the cursor to a point in physical pixels, without a mouse event.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    /// <summary>Returns the state of a key or button right now; the high bit is set while it is down.</summary>
    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    /// <summary>Sets the opacity of a layered window.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>Puts a message in the queue of the thread that owns the window, without waiting.</summary>
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>True when the window has the visible style.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    /// <summary>Copies the window title into the buffer. Returns its length, or zero.</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static unsafe partial int GetWindowText(IntPtr hWnd, char* lpString, int nMaxCount);

    /// <summary>Returns the thread that created the window and gives the process id.</summary>
    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>Returns the title of a window, at most 511 characters, or an empty string.</summary>
    public static unsafe string GetWindowText(IntPtr hWnd)
    {
        const int Capacity = 512;
        var buffer = stackalloc char[Capacity];
        var length = GetWindowText(hWnd, buffer, Capacity);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}
