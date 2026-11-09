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

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetProp(IntPtr hwnd, string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

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
}
