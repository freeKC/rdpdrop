using System.Runtime.InteropServices;

namespace RdpDrop.Agent.Interop;

/// <summary>
/// Shell functions (shell32.dll) and the few COM interfaces needed to tell Explorer
/// tabs apart.
/// </summary>
internal static partial class Shell32
{
    /// <summary>FOLDERID_Downloads.</summary>
    public static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>IID_IShellBrowser, also used as the service id when querying Explorer windows.</summary>
    public static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    /// <summary>
    /// Returns the path of a known folder in <paramref name="ppszPath"/>, which the caller
    /// frees with CoTaskMemFree. Returns an HRESULT.
    /// </summary>
    [LibraryImport("shell32.dll")]
    public static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    /// <summary>Path of a known folder, or null when Windows cannot give it.</summary>
    public static string? GetKnownFolderPath(Guid folderId)
    {
        var hr = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out var pointer);
        try
        {
            return hr == 0 ? Marshal.PtrToStringUni(pointer) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }
}

/// <summary>
/// IServiceProvider (COM): asks an object for a service it can provide. Implemented by
/// the items of Shell.Application.Windows().
/// </summary>
[ComImport]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleServiceProvider
{
    /// <summary>Returns the interface <paramref name="riid"/> of the service <paramref name="guidService"/>.</summary>
    [PreserveSig]
    int QueryService(in Guid guidService, in Guid riid, out IntPtr ppvObject);
}

/// <summary>
/// IOleWindow (COM): base interface of IShellBrowser. GetWindow on the browser of an
/// Explorer tab returns the window of that tab.
/// </summary>
[ComImport]
[Guid("00000114-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleWindow
{
    /// <summary>Returns the window handle of the object.</summary>
    [PreserveSig]
    int GetWindow(out IntPtr phwnd);

    /// <summary>Enters or leaves context sensitive help mode. Not used.</summary>
    [PreserveSig]
    int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
}
