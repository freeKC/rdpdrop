using System.Runtime.InteropServices;
using RdpDrop.Agent.Interop;
using RdpDrop.Agent.Logging;
using RdpDrop.Core.Targets;

namespace RdpDrop.Agent.Drops;

/// <summary>
/// Finds what is under a screen point: the desktop, an Explorer window and the folder it
/// shows, or something else. Must run on an STA thread because it talks to the shell
/// through COM.
/// </summary>
internal sealed class CursorTargetResolver
{
    private const string ShellTabWindowClass = "ShellTabWindowClass";

    private readonly FileLogger _logger;

    public CursorTargetResolver(FileLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Describes the window at (<paramref name="x"/>, <paramref name="y"/>), in physical
    /// pixels of the session desktop.
    /// </summary>
    public CursorTarget Find(int x, int y)
    {
        var window = User32.WindowFromPoint(new User32.Point { X = x, Y = y });
        if (window == IntPtr.Zero)
        {
            _logger.Debug($"No window at ({x}, {y}).");
            return new CursorTarget(CursorTargetKind.Other);
        }

        var root = User32.GetAncestor(window, User32.GaRoot);
        if (root == IntPtr.Zero)
        {
            root = window;
        }

        var className = User32.GetClassName(root);
        _logger.Debug($"Window at ({x}, {y}): 0x{window:X}, root 0x{root:X}, class {className}.");
        if (className is "Progman" or "WorkerW")
        {
            return new CursorTarget(CursorTargetKind.Desktop);
        }

        try
        {
            return FindExplorerFolder(root, window);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            _logger.Warn($"Could not ask the shell which folder is under the cursor. {ex.Message}");
            return new CursorTarget(CursorTargetKind.Other);
        }
    }

    private CursorTarget FindExplorerFolder(IntPtr root, IntPtr windowUnderCursor)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: false);
        if (shellType is null)
        {
            _logger.Warn("Shell.Application is not available.");
            return new CursorTarget(CursorTargetKind.Other);
        }

        // Windows 11 Explorer shows tabs in one top level window. Every tab is a separate
        // item of Shell.Application.Windows() with the same HWND, so the tab under the
        // cursor (or else the active tab, the topmost ShellTabWindowClass child) decides.
        var activeTab = User32.FindWindowEx(root, IntPtr.Zero, ShellTabWindowClass, null);

        dynamic? shell = null;
        dynamic? windows = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return new CursorTarget(CursorTargetKind.Other);
            }

            windows = shell.Windows();
            int count = windows.Count;
            CursorTarget? sameRoot = null;
            for (var index = 0; index < count; index++)
            {
                dynamic? item = null;
                try
                {
                    item = windows.Item(index);
                    if (item is null)
                    {
                        continue;
                    }

                    object? itemHandle = item.HWND;
                    if (ToHandle(itemHandle) != root)
                    {
                        continue;
                    }

                    var tab = GetTabWindow((object)item);
                    var isTabUnderCursor = tab != IntPtr.Zero && (tab == windowUnderCursor || User32.IsChild(tab, windowUnderCursor));
                    var isActiveTab = activeTab == IntPtr.Zero || tab == IntPtr.Zero || tab == activeTab;
                    if (!isTabUnderCursor && !isActiveTab)
                    {
                        continue;
                    }

                    var target = DescribeFolder((object)item);
                    if (isTabUnderCursor)
                    {
                        return target;
                    }

                    sameRoot ??= target;
                }
                finally
                {
                    Release(item);
                }
            }

            return sameRoot ?? new CursorTarget(CursorTargetKind.Other);
        }
        finally
        {
            Release(windows);
            Release(shell);
        }
    }

    private CursorTarget DescribeFolder(object item)
    {
        string? path = null;
        try
        {
            dynamic window = item;
            path = (string?)window.Document.Folder.Self.Path;
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            // The window does not show a folder view.
            _logger.Debug($"The Explorer window does not show a folder. {ex.Message}");
        }

        _logger.Debug($"Explorer window shows \"{path}\".");
        return TargetFolderPolicy.IsFileSystemPath(path)
            ? new CursorTarget(CursorTargetKind.ExplorerFolder, path)
            : new CursorTarget(CursorTargetKind.VirtualFolder, path);
    }

    /// <summary>Window of the Explorer tab that hosts <paramref name="item"/>, or zero.</summary>
    private static IntPtr GetTabWindow(object item)
    {
        if (item is not IOleServiceProvider provider)
        {
            return IntPtr.Zero;
        }

        var iid = Shell32.IidShellBrowser;
        if (provider.QueryService(iid, iid, out var browserPointer) != 0 || browserPointer == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            var browser = Marshal.GetObjectForIUnknown(browserPointer);
            try
            {
                return browser is IOleWindow window && window.GetWindow(out var hwnd) == 0 ? hwnd : IntPtr.Zero;
            }
            finally
            {
                Marshal.ReleaseComObject(browser);
            }
        }
        finally
        {
            Marshal.Release(browserPointer);
        }
    }

    private static IntPtr ToHandle(object? value) => value switch
    {
        int i => new IntPtr(i),
        long l => new IntPtr(l),
        null => IntPtr.Zero,
        _ => new IntPtr(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
    };

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }
}
