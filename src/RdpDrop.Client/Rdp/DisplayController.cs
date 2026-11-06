using System.Globalization;
using System.Runtime.InteropServices;
using MSTSCLib;
using RdpDrop.Client.Logging;
using RdpDrop.Client.Settings;
using RdpDrop.Core.Display;

namespace RdpDrop.Client.Rdp;

/// <summary>
/// Keeps the remote desktop in step with the window: display mode (resize the session or
/// scale the picture), scale factors, and the session size after a resize or a change of
/// full screen state.
/// </summary>
/// <remarks>
/// In "resize session" mode a resize is debounced, then sent with
/// IMsRdpClient9.UpdateSessionDisplaySettings. When the control or the server refuses it,
/// the failure is logged once and IMsRdpClient8.Reconnect is used from then on, until
/// the next connection started by the user. Every call happens on the UI thread.
/// </remarks>
internal sealed class DisplayController : IDisposable
{
    private const int DebounceMilliseconds = 300;

    private readonly RdpControl _control;
    private readonly Control _owner;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = DebounceMilliseconds };

    private bool _loggedOn;
    private bool _fullScreen;
    private bool _updateRefused;
    private bool _forceNextUpdate;
    private Size _requested;
    private int _desktopScale = SessionDisplay.DefaultScaleFactor;
    private int _deviceScale = SessionDisplay.DefaultScaleFactor;
    private bool _multimon;

    public DisplayController(RdpControl control, Control owner, DisplayMode mode)
    {
        _control = control;
        _owner = owner;
        Mode = mode;
        _timer.Tick += (_, _) => OnTimer();
    }

    public DisplayMode Mode { get; private set; }

    /// <summary>Size of the remote desktop, from the connection and from every size change event.</summary>
    public Size RemoteDesktopSize { get; private set; }

    /// <summary>Scale factor that "Auto" gives on the monitor that holds the window.</summary>
    public int AutoScaleFactor => SessionDisplay.DesktopScaleFromDpi(_owner.DeviceDpi);

    /// <summary>
    /// Computes the options of the next connection. <paramref name="scaleChoice"/> is the
    /// desktop scale factor in percent, or null for <see cref="AutoScaleFactor"/>.
    /// </summary>
    public SessionOptions PrepareConnection(int? scaleChoice, int keyboardHookMode, bool useMultimon, string fullScreenTitle)
    {
        _desktopScale = scaleChoice ?? AutoScaleFactor;
        _deviceScale = SessionDisplay.DeviceScaleFor(_desktopScale);
        _multimon = useMultimon;
        _updateRefused = false;
        _loggedOn = false;
        _fullScreen = false;
        var origin = scaleChoice is null ? "auto" : "chosen";
        ClientLog.Info(Invariant(
            $"Display for the next connection: mode {Mode}, monitor at {_owner.DeviceDpi} DPI, scale factor {_desktopScale} % ({origin}), device scale factor {_deviceScale} %"));
        return new SessionOptions(Mode == DisplayMode.ScaleToWindow, _desktopScale, _deviceScale, keyboardHookMode, useMultimon, fullScreenTitle);
    }

    /// <summary>Records the desktop size requested by the connection that starts.</summary>
    public void OnConnecting(Size desktopSize)
    {
        RemoteDesktopSize = desktopSize;
        _requested = desktopSize;
    }

    /// <summary>The session is ready for display updates once the user is logged on.</summary>
    public void OnLoggedOn()
    {
        _loggedOn = true;
        if (Mode == DisplayMode.ResizeSession)
        {
            ScheduleUpdate("logged on");
        }
    }

    public void OnDisconnected()
    {
        _loggedOn = false;
        _fullScreen = false;
        _timer.Stop();
    }

    public void OnRemoteDesktopSizeChange(int width, int height)
    {
        ClientLog.Info(Invariant($"Remote desktop size is now {width}x{height}"));
        if (width > 0 && height > 0)
        {
            RemoteDesktopSize = new Size(width, height);
        }
    }

    /// <summary>Called on every resize of the window; the update waits until the resize settles.</summary>
    public void OnWindowResized()
    {
        if (Mode == DisplayMode.ResizeSession && _loggedOn && !_fullScreen)
        {
            _timer.Stop();
            _timer.Start();
        }
    }

    public void OnFullScreenChanged(bool fullScreen)
    {
        _fullScreen = fullScreen;
        if (Mode == DisplayMode.ResizeSession && _loggedOn)
        {
            ScheduleUpdate(fullScreen ? "entered full screen" : "left full screen");
        }
    }

    /// <summary>
    /// Changes the mode. While connected, smart sizing follows at once and the
    /// "resize session" mode sends one update.
    /// </summary>
    public void SetMode(DisplayMode mode, bool connected)
    {
        if (mode == Mode)
        {
            return;
        }

        Mode = mode;
        ClientLog.Info($"Display mode is now {mode}");
        if (!connected)
        {
            return;
        }

        var smartSizing = mode == DisplayMode.ScaleToWindow;
        try
        {
            _control.AdvancedSettings9.SmartSizing = smartSizing;
            ClientLog.Info($"Smart sizing set to {smartSizing} while connected");
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or AxHost.InvalidActiveXStateException)
        {
            ClientLog.Warn(Invariant($"Smart sizing {smartSizing} refused by the control (0x{ex.HResult:X8})"));
        }

        if (mode == DisplayMode.ResizeSession && _loggedOn)
        {
            ScheduleUpdate("mode changed");
        }
    }

    public void Dispose() => _timer.Dispose();

    private void ScheduleUpdate(string reason)
    {
        ClientLog.Info($"Display update scheduled: {reason}");
        _forceNextUpdate = true;
        _timer.Stop();
        _timer.Start();
    }

    private void OnTimer()
    {
        _timer.Stop();
        var force = _forceNextUpdate;
        _forceNextUpdate = false;
        if (Mode != DisplayMode.ResizeSession || !_loggedOn || _owner.FindForm()?.WindowState == FormWindowState.Minimized)
        {
            return;
        }

        if (_fullScreen && _multimon)
        {
            SyncWithMonitors();
            return;
        }

        var view = _control.ViewSize;
        var (width, height) = SessionDisplay.ClampSize(view.Width, view.Height);
        var target = new Size(width, height);
        if (target == RemoteDesktopSize || (!force && target == _requested))
        {
            return;
        }

        _requested = target;
        if (!_updateRefused && TryUpdateSessionDisplay(target))
        {
            return;
        }

        TryReconnect(target);
    }

    private bool TryUpdateSessionDisplay(Size target)
    {
        var (physicalWidth, physicalHeight) = SessionDisplay.PhysicalMillimeters(target.Width, target.Height, _owner.DeviceDpi);
        try
        {
            if (_control.GetOcx() is not IMsRdpClient9 client)
            {
                throw new InvalidCastException("IMsRdpClient9 is not available.");
            }

            client.UpdateSessionDisplaySettings(
                (uint)target.Width,
                (uint)target.Height,
                (uint)physicalWidth,
                (uint)physicalHeight,
                0,
                (uint)_desktopScale,
                (uint)_deviceScale);
            ClientLog.Info(Invariant(
                $"Display update sent: {target.Width}x{target.Height}, physical {physicalWidth}x{physicalHeight} mm, scale {_desktopScale} %, device scale {_deviceScale} % (remote was {RemoteDesktopSize.Width}x{RemoteDesktopSize.Height})"));
            return true;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException or AxHost.InvalidActiveXStateException)
        {
            _updateRefused = true;
            ClientLog.Warn(Invariant($"UpdateSessionDisplaySettings refused (0x{ex.HResult:X8}: {ex.Message}); using Reconnect for the rest of this connection"));
            return false;
        }
    }

    private void TryReconnect(Size target)
    {
        try
        {
            if (_control.GetOcx() is not IMsRdpClient8 client)
            {
                ClientLog.Warn("IMsRdpClient8 is not available, the session keeps its size");
                return;
            }

            var status = client.Reconnect((uint)target.Width, (uint)target.Height);
            ClientLog.Info(Invariant($"Display fallback: Reconnect({target.Width}, {target.Height}) returned {status}"));
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or AxHost.InvalidActiveXStateException)
        {
            ClientLog.Warn(Invariant($"Reconnect({target.Width}, {target.Height}) failed (0x{ex.HResult:X8}: {ex.Message}); the session keeps its size"));
        }
    }

    /// <summary>
    /// With every monitor in use, the control knows the monitor layout; a single size
    /// would collapse it, so the control is asked to send its own layout instead.
    /// </summary>
    private void SyncWithMonitors()
    {
        try
        {
            if (_control.GetOcx() is IMsRdpClient9 client)
            {
                client.SyncSessionDisplaySettings();
                ClientLog.Info("Display update sent: SyncSessionDisplaySettings for all monitors");
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or AxHost.InvalidActiveXStateException)
        {
            ClientLog.Warn(Invariant($"SyncSessionDisplaySettings failed (0x{ex.HResult:X8}: {ex.Message})"));
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
