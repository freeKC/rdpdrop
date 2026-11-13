using System.Runtime.InteropServices;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Rdp;

namespace UltimateRDP.Client;

/// <summary>
/// Closes the open menu of the main window when the user clicks in the remote session, since
/// 0.9.1. Windows Forms closes a menu at a click that the thread of the window receives, and
/// the Remote Desktop control takes the mouse in a window of a thread of its own, so a click
/// in the session left the menu open.
/// </summary>
/// <remarks>
/// While the menu is active, and only then, a timer looks at the state of the mouse buttons.
/// A button pressed over a window that is not a menu is told to Windows Forms as a click
/// outside the menus, with a mouse message posted to a hidden window of this class, so that
/// the menu closes by the same path as for any other click. No hook is installed, and no
/// input is sent to another window.
/// </remarks>
internal sealed class MenuClickAway : IDisposable
{
    private const int CheckMilliseconds = 30;
    private const int WmMButtonDown = 0x0207;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;
    private const int WsPopup = unchecked((int)0x80000000);

    /// <summary>A point that no menu covers, in the coordinates of the hidden window, as the lParam of a mouse message.</summary>
    private static readonly IntPtr FarAway = new(unchecked((int)0x8300_8300));

    private readonly MenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = CheckMilliseconds };
    private readonly NativeWindow _sink = new();
    private bool _told;

    public MenuClickAway(MenuStrip menu)
    {
        _menu = menu;
        _menu.MenuActivate += OnMenuActivate;
        _menu.MenuDeactivate += OnMenuDeactivate;
        _timer.Tick += (_, _) => Check();
    }

    public void Dispose()
    {
        _menu.MenuActivate -= OnMenuActivate;
        _menu.MenuDeactivate -= OnMenuDeactivate;
        _timer.Dispose();
        if (_sink.Handle != IntPtr.Zero)
        {
            _sink.DestroyHandle();
        }
    }

    private static bool IsDown(int key) => GetAsyncKeyState(key) != 0;

    private void OnMenuActivate(object? sender, EventArgs e)
    {
        // Reading the keys once forgets the presses of before the menu.
        _ = IsDown(VkLButton) | IsDown(VkRButton) | IsDown(VkMButton);
        _told = false;
        _timer.Start();
    }

    private void OnMenuDeactivate(object? sender, EventArgs e) => _timer.Stop();

    private void Check()
    {
        if (!(IsDown(VkLButton) | IsDown(VkRButton) | IsDown(VkMButton)))
        {
            return;
        }

        var under = NativeMethods.WindowFromPoint(Cursor.Position);
        if (under != IntPtr.Zero && Control.FromChildHandle(under) is ToolStrip)
        {
            // A click in the menu bar or in an open menu: Windows Forms handles it.
            return;
        }

        if (!_told)
        {
            // First the path of Windows Forms itself: its filter of the mouse messages of this
            // thread closes the menus at a click that is in none of them.
            _told = true;
            if (_sink.Handle == IntPtr.Zero)
            {
                _sink.CreateHandle(new CreateParams { Style = WsPopup });
            }

            ClientLog.Debug("A mouse button went down outside the open menu, in a window of another thread: the menu is told to close.");
            PostMessage(_sink.Handle, WmMButtonDown, IntPtr.Zero, FarAway);
            return;
        }

        // The menu is still active at the next look: its open lists are closed directly.
        _timer.Stop();
        foreach (ToolStripItem item in _menu.Items)
        {
            if (item is ToolStripDropDownItem { HasDropDown: true, DropDown.Visible: true } open)
            {
                open.DropDown.Close(ToolStripDropDownCloseReason.AppClicked);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
