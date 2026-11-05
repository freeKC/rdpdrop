using System.Runtime.InteropServices;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace RdpDrop.Client.Rdp;

/// <summary>
/// The OLE IDropTarget interface. Public so that the runtime can expose it to COM.
/// </summary>
[ComImport]
[Guid("00000122-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleDropTarget
{
    [PreserveSig]
    int OleDragEnter([In, MarshalAs(UnmanagedType.Interface)] ComDataObject? dataObject, [In] int keyState, [In] OlePoint pt, [In, Out] ref int effect);

    [PreserveSig]
    int OleDragOver([In] int keyState, [In] OlePoint pt, [In, Out] ref int effect);

    [PreserveSig]
    int OleDragLeave();

    [PreserveSig]
    int OleDrop([In, MarshalAs(UnmanagedType.Interface)] ComDataObject? dataObject, [In] int keyState, [In] OlePoint pt, [In, Out] ref int effect);
}

/// <summary>
/// The OLE POINTL structure: a point in screen coordinates.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct OlePoint
{
    public int X;
    public int Y;
}
