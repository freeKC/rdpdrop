using RdpDrop.Client.Logging;
using RdpDrop.Client.Settings;

namespace RdpDrop.Client;

/// <summary>
/// View menu and full screen keys of the main form. Every choice acts on the active
/// session and becomes the saved choice for the sessions opened later.
/// </summary>
internal sealed partial class MainForm
{
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Ctrl+Alt+Pause arrives as Cancel on most keyboards, because Ctrl turns Pause into Break.
        if (keyData is Keys.F11
            or (Keys.Control | Keys.Alt | Keys.Pause)
            or (Keys.Control | Keys.Alt | Keys.Cancel))
        {
            ActiveSession?.ToggleFullScreen(keyData == Keys.F11 ? "F11" : "Ctrl+Alt+Pause");
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void InitializeViewMenu()
    {
        _viewMenu.DisplayModeChosen += (_, mode) => OnDisplayModeChosen(mode);
        _viewMenu.ScaleFactorChosen += (_, factor) => OnScaleFactorChosen(factor);
        _viewMenu.FullScreenClicked += (_, _) => ActiveSession?.ToggleFullScreen("menu");
        _viewMenu.AllMonitorsChosen += (_, value) => OnAllMonitorsChosen(value);
        _viewMenu.KeyboardHookModeChosen += (_, mode) => OnKeyboardHookModeChosen(mode);
        _viewMenu.Root.DropDownOpening += (_, _) => ShowViewChoices();
    }

    /// <summary>
    /// Shows the choices of the active session, or the saved ones when there is none.
    /// </summary>
    private void ShowViewChoices()
    {
        var session = ActiveSession;
        if (session is null)
        {
            _viewMenu.ShowDisplayMode(_settings.DisplayMode);
            _viewMenu.ShowScaleFactor(_settings.ScaleFactor, Core.Display.SessionDisplay.DesktopScaleFromDpi(DeviceDpi));
            _viewMenu.ShowAllMonitors(_settings.UseAllMonitors);
            _viewMenu.ShowKeyboardHookMode(_settings.KeyboardHookMode);
            _viewMenu.ShowFullScreen(false, false);
            return;
        }

        _viewMenu.ShowDisplayMode(session.DisplayMode);
        _viewMenu.ShowScaleFactor(session.ScaleChoice, session.AutoDesktopScaleFactor);
        _viewMenu.ShowAllMonitors(session.UseAllMonitors);
        _viewMenu.ShowKeyboardHookMode(session.KeyboardHookMode);
        _viewMenu.ShowFullScreen(session.IsFullScreen, session.CanToggleFullScreen);
    }

    private void OnDisplayModeChosen(DisplayMode mode)
    {
        _settings.DisplayMode = mode;
        SaveViewChoices();
        ActiveSession?.SetDisplayMode(mode);
    }

    private void OnScaleFactorChosen(int? factor)
    {
        _settings.ScaleFactor = factor;
        SaveViewChoices();
        ActiveSession?.SetScaleFactor(factor);
    }

    private void OnAllMonitorsChosen(bool useAllMonitors)
    {
        _settings.UseAllMonitors = useAllMonitors;
        SaveViewChoices();
        ActiveSession?.SetUseAllMonitors(useAllMonitors);
    }

    private void OnKeyboardHookModeChosen(int mode)
    {
        _settings.KeyboardHookMode = mode;
        SaveViewChoices();
        ActiveSession?.SetKeyboardHookMode(mode);
    }

    private void SaveViewChoices()
    {
        ClientLog.Info($"Display choices changed: {_settings}");
        _settings.Save(ClientSettings.DefaultPath);
        ShowViewChoices();
    }
}
