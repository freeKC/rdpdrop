using System.Globalization;
using System.Runtime.InteropServices;
using RdpDrop.Client.Logging;
using RdpDrop.Client.Settings;

namespace RdpDrop.Client;

/// <summary>
/// View menu choices of the main form: display mode, scale factor, full screen, monitors
/// and Windows keys. The size logic itself lives in <see cref="Rdp.DisplayController"/>.
/// </summary>
internal sealed partial class MainForm
{
    private int? _scaleChoice;
    private int _keyboardHookMode;
    private bool _useAllMonitors;
    private bool _sessionUsesAllMonitors;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Ctrl+Alt+Pause arrives as Cancel on most keyboards, because Ctrl turns Pause into Break.
        if (keyData is Keys.F11
            or (Keys.Control | Keys.Alt | Keys.Pause)
            or (Keys.Control | Keys.Alt | Keys.Cancel))
        {
            ToggleFullScreen(keyData == Keys.F11 ? "F11" : "Ctrl+Alt+Pause");
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        // The base constructor resizes the form before the controller exists.
        _display?.OnWindowResized();
    }

    /// <summary>
    /// Starts from the saved choices; the keys of the .rdp file win over them when present.
    /// </summary>
    private void InitializeViewChoices()
    {
        ClientLog.Info($"Saved display choices: {_settings}");
        _scaleChoice = _file.DesktopScaleFactor ?? _settings.ScaleFactor;
        _keyboardHookMode = _file.KeyboardHook ?? _settings.KeyboardHookMode;
        _useAllMonitors = _file.UseMultimon ?? _settings.UseAllMonitors;
        LogOverride("desktopscalefactor", _file.DesktopScaleFactor);
        LogOverride("keyboardhook", _file.KeyboardHook);
        LogOverride("use multimon", _file.UseMultimon);

        _viewMenu.DisplayModeChosen += (_, mode) => OnDisplayModeChosen(mode);
        _viewMenu.ScaleFactorChosen += (_, factor) => OnScaleFactorChosen(factor);
        _viewMenu.FullScreenClicked += (_, _) => ToggleFullScreen("menu");
        _viewMenu.AllMonitorsChosen += (_, value) => OnAllMonitorsChosen(value);
        _viewMenu.KeyboardHookModeChosen += (_, mode) => OnKeyboardHookModeChosen(mode);
        _viewMenu.Root.DropDownOpening += (_, _) => ShowViewChoices();
        ShowViewChoices();
    }

    private static void LogOverride<T>(string key, T? value)
    {
        if (value is not null)
        {
            ClientLog.Info(string.Create(CultureInfo.InvariantCulture, $"The .rdp file sets {key} to {value}; it is used instead of the saved choice."));
        }
    }

    private void ShowViewChoices()
    {
        _viewMenu.ShowDisplayMode(_display.Mode);
        _viewMenu.ShowScaleFactor(_scaleChoice, _display.AutoScaleFactor);
        _viewMenu.ShowAllMonitors(_useAllMonitors);
        _viewMenu.ShowKeyboardHookMode(_keyboardHookMode);
    }

    private void OnDisplayModeChosen(DisplayMode mode)
    {
        _display.SetMode(mode, connected: _state != ConnectionState.Disconnected);
        _settings.DisplayMode = mode;
        SaveViewChoices();
    }

    private void OnScaleFactorChosen(int? factor)
    {
        if (factor == _scaleChoice)
        {
            return;
        }

        _scaleChoice = factor;
        _settings.ScaleFactor = factor;
        SaveViewChoices();
        ApplyAfterReconnect("scale factor");
    }

    private void OnAllMonitorsChosen(bool useAllMonitors)
    {
        _useAllMonitors = useAllMonitors;
        _settings.UseAllMonitors = useAllMonitors;
        SaveViewChoices();
        ApplyAfterReconnect("monitor setting");
    }

    /// <summary>
    /// The control documents that KeyboardHookMode cannot be set while connected, so a
    /// change always waits for a new connection.
    /// </summary>
    private void OnKeyboardHookModeChosen(int mode)
    {
        if (mode == _keyboardHookMode)
        {
            return;
        }

        _keyboardHookMode = mode;
        _settings.KeyboardHookMode = mode;
        SaveViewChoices();
        ApplyAfterReconnect("Windows keys setting");
    }

    private void SaveViewChoices()
    {
        ClientLog.Info($"Display choices changed: {_settings}");
        _settings.Save(ClientSettings.DefaultPath);
        ShowViewChoices();
    }

    /// <summary>
    /// A setting that the control only reads when connecting: offers to reconnect now when
    /// a session is open, otherwise it applies to the next connection.
    /// </summary>
    private void ApplyAfterReconnect(string setting)
    {
        if (_state == ConnectionState.Disconnected)
        {
            ShowDropStatus($"The new {setting} applies when you connect.");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"The new {setting} applies to a new connection.{Environment.NewLine}{Environment.NewLine}Reconnect now?",
            AppInfo.Name,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            ShowDropStatus($"The new {setting} applies at the next connection.");
            return;
        }

        ClientLog.Info($"Reconnecting for the new {setting}.");
        if (_state == ConnectionState.Disconnected)
        {
            // The session ended while the question was open.
            Connect();
            return;
        }

        _reconnectRequested = true;
        Disconnect();
    }

    private void ToggleFullScreen(string source)
    {
        if (_state != ConnectionState.Connected)
        {
            ClientLog.Info($"Full screen requested from {source} while {_state}, ignored");
            ShowDropStatus("Full screen is available once connected.");
            return;
        }

        try
        {
            var target = !_rdp.FullScreen;
            ClientLog.Info($"Full screen {(target ? "on" : "off")} requested from {source}");
            _rdp.FullScreen = target;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or AxHost.InvalidActiveXStateException)
        {
            ClientLog.Warn(string.Create(CultureInfo.InvariantCulture, $"Full screen refused by the control (0x{ex.HResult:X8}: {ex.Message})"));
        }
    }

    /// <summary>
    /// OnEnterFullScreenMode and OnLeaveFullScreenMode: the control covers the monitor of
    /// the window, or every monitor when the session uses them all.
    /// </summary>
    private void OnFullScreenChanged(bool fullScreen)
    {
        var bounds = _sessionUsesAllMonitors ? SystemInformation.VirtualScreen : Screen.FromControl(this).Bounds;
        ClientLog.Info(fullScreen
            ? string.Create(CultureInfo.InvariantCulture, $"Event OnEnterFullScreenMode, area {bounds.Width}x{bounds.Height} at ({bounds.X}, {bounds.Y})")
            : "Event OnLeaveFullScreenMode");
        ShowFullScreenState(fullScreen, bounds);
        _display.OnFullScreenChanged(fullScreen);
    }

    private void ShowFullScreenState(bool fullScreen, Rectangle bounds = default)
    {
        _rdp.FullScreenBounds = fullScreen ? bounds : null;
        _viewMenu.ShowFullScreen(fullScreen, _state == ConnectionState.Connected);
    }
}
