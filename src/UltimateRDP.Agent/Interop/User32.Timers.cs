using System.Runtime.InteropServices;

namespace UltimateRDP.Agent.Interop;

/// <summary>
/// Window timers and window rectangles (user32.dll).
/// </summary>
internal static partial class User32
{
    /// <summary>WM_TIMER: a timer of SetTimer elapsed; wParam is its id.</summary>
    public const int WmTimer = 0x0113;

    /// <summary>RECT: a rectangle of the screen, the right and bottom edges excluded.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// Starts or restarts timer <paramref name="id"/> of the window: WM_TIMER comes to the
    /// window every <paramref name="milliseconds"/>, through the message loop of its thread,
    /// so no thread is added. Returns zero on failure.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nuint SetTimer(IntPtr hWnd, nuint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    /// <summary>Stops a timer started with <see cref="SetTimer"/>.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(IntPtr hWnd, nuint uIDEvent);

    /// <summary>Gives the rectangle of a window on the screen.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out Rect rect);
}
