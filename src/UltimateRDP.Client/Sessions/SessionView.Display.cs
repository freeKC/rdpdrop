using System.Globalization;
using System.Runtime.InteropServices;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Client.Settings;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// Display choices of the session: display mode, scale factor, full screen, monitors and
/// Windows keys. The size logic itself lives in <see cref="Rdp.DisplayController"/>.
/// </summary>
internal sealed partial class SessionView
{
    private int? _scaleChoice;
    private int _keyboardHookMode;
    private bool _useAllMonitors;
    private bool _sessionUsesAllMonitors;

    public DisplayMode DisplayMode => _display.Mode;

    /// <summary>Desktop scale factor in percent, or null for <see cref="AutoDesktopScaleFactor"/>.</summary>
    public int? ScaleChoice => _scaleChoice;

    /// <summary>Scale factor that "Auto" gives on the monitor that holds the session.</summary>
    public int AutoDesktopScaleFactor => _display.AutoScaleFactor;

    public bool UseAllMonitors => _useAllMonitors;

    /// <summary>0: Windows keys stay on this computer, 1: in the session, 2: in the session in full screen.</summary>
    public int KeyboardHookMode => _keyboardHookMode;

    /// <summary>True while the control runs full screen.</summary>
    public bool IsFullScreen => _rdp.FullScreenBounds is not null;

    /// <summary>Full screen can only be toggled while connected, and never for a RemoteApp program.</summary>
    public bool CanToggleFullScreen => IsConnected && !IsRemoteApp;

    /// <summary>
    /// False for a RemoteApp tab: the display mode, the scale factor, full screen and the
    /// monitors do not apply to the windows of a program.
    /// </summary>
    public bool HasDisplayChoices => !IsRemoteApp;

    public void SetDisplayMode(DisplayMode mode)
    {
        if (IsRemoteApp)
        {
            return;
        }

        _display.SetMode(mode, connected: State != SessionState.Disconnected);
        RaiseStateChanged();
    }

    /// <summary>Sets the scale factor in percent, or null for Auto, and offers to reconnect.</summary>
    public void SetScaleFactor(int? factor)
    {
        if (factor == _scaleChoice || IsRemoteApp)
        {
            return;
        }

        _scaleChoice = factor;
        RaiseStateChanged();
        ApplyAfterReconnect("scale factor", LocalText.Of(nameof(Strings.SettingScaleFactor)));
    }

    public void SetUseAllMonitors(bool useAllMonitors)
    {
        if (useAllMonitors == _useAllMonitors || IsRemoteApp)
        {
            return;
        }

        _useAllMonitors = useAllMonitors;
        RaiseStateChanged();
        ApplyAfterReconnect("monitor setting", LocalText.Of(nameof(Strings.SettingMonitors)));
    }

    /// <summary>
    /// The control documents that KeyboardHookMode cannot be set while connected, so a
    /// change always waits for a new connection.
    /// </summary>
    public void SetKeyboardHookMode(int mode)
    {
        if (mode == _keyboardHookMode)
        {
            return;
        }

        _keyboardHookMode = mode;
        RaiseStateChanged();
        ApplyAfterReconnect("Windows keys setting", LocalText.Of(nameof(Strings.SettingWindowsKeys)));
    }

    public void ToggleFullScreen(string source)
    {
        if (IsRemoteApp)
        {
            ClientLog.Info($"Full screen requested from {source} for the RemoteApp tab of {Host}, ignored");
            return;
        }

        if (State != SessionState.Connected)
        {
            ClientLog.Info($"Full screen requested from {source} while {State}, ignored");
            ShowDropStatus(LocalText.Of(nameof(Strings.FullScreenNeedsConnection)));
            return;
        }

        try
        {
            var target = !_rdp.FullScreen;
            ClientLog.Info($"Full screen {(target ? "on" : "off")} for {Host} requested from {source}");
            _rdp.FullScreen = target;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or AxHost.InvalidActiveXStateException)
        {
            ClientLog.Warn(string.Create(CultureInfo.InvariantCulture, $"Full screen refused by the control (0x{ex.HResult:X8}: {ex.Message})"));
        }
    }

    /// <summary>
    /// Starts from the saved choices; the keys of the .rdp file win over them when present.
    /// </summary>
    private void InitializeViewChoices(ClientSettings settings)
    {
        ClientLog.Info($"Saved display choices: {settings}");
        _scaleChoice = RdpFile.DesktopScaleFactor ?? settings.ScaleFactor;
        _keyboardHookMode = RdpFile.KeyboardHook ?? settings.KeyboardHookMode;
        _useAllMonitors = RdpFile.UseMultimon ?? settings.UseAllMonitors;
        LogOverride("desktopscalefactor", RdpFile.DesktopScaleFactor);
        LogOverride("keyboardhook", RdpFile.KeyboardHook);
        LogOverride("use multimon", RdpFile.UseMultimon);
    }

    private static void LogOverride<T>(string key, T? value)
    {
        if (value is not null)
        {
            ClientLog.Info(string.Create(CultureInfo.InvariantCulture, $"The .rdp file sets {key} to {value}; it is used instead of the saved choice."));
        }
    }

    /// <summary>
    /// A setting that the control only reads when connecting: offers to reconnect now when
    /// a session is open, otherwise it applies to the next connection.
    /// <paramref name="setting"/> names it in the log, <paramref name="settingText"/> for
    /// the user, as the start of a sentence such as "The new scale factor".
    /// </summary>
    private void ApplyAfterReconnect(string setting, LocalText settingText)
    {
        if (State == SessionState.Disconnected)
        {
            ShowDropStatus(LocalText.Of(nameof(Strings.SettingAppliesWhenConnecting), settingText));
            return;
        }

        var answer = MessageBox.Show(
            FindForm(),
            LocalText.Of(nameof(Strings.SettingReconnectQuestion), settingText, Host).ToString(),
            AppInfo.Name,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            ShowDropStatus(LocalText.Of(nameof(Strings.SettingAppliesNextConnection), settingText));
            return;
        }

        ClientLog.Info($"Reconnecting to {Host} for the new {setting}.");
        if (State == SessionState.Disconnected)
        {
            // The session ended while the question was open.
            Connect();
            return;
        }

        _reconnectRequested = true;
        Disconnect();
    }

    /// <summary>
    /// OnEnterFullScreenMode and OnLeaveFullScreenMode: the control covers the monitor of
    /// the view, or every monitor when the session uses them all.
    /// </summary>
    private void OnFullScreenChanged(bool fullScreen)
    {
        var bounds = _sessionUsesAllMonitors ? SystemInformation.VirtualScreen : Screen.FromControl(this).Bounds;
        ClientLog.Info(fullScreen
            ? string.Create(CultureInfo.InvariantCulture, $"Event OnEnterFullScreenMode ({Host}), area {bounds.Width}x{bounds.Height} at ({bounds.X}, {bounds.Y})")
            : $"Event OnLeaveFullScreenMode ({Host})");
        ShowFullScreenState(fullScreen, bounds);
        _display.OnFullScreenChanged(fullScreen);
    }

    private void ShowFullScreenState(bool fullScreen, Rectangle bounds = default)
    {
        _rdp.FullScreenBounds = fullScreen ? bounds : null;
        RaiseStateChanged();
    }
}
