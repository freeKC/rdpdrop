using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Settings;

namespace UltimateRDP.Client;

/// <summary>
/// View menu and full screen keys of the main form. Every display choice acts on the
/// active session and becomes the saved choice for the sessions opened later. The
/// language applies at once to the whole window.
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

        if (ProcessFileShortcut(keyData))
        {
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
        _viewMenu.LanguageChosen += (_, choice) => OnLanguageChosen(choice);
        _viewMenu.Root.DropDownOpening += (_, _) => ShowViewChoices();
    }

    /// <summary>
    /// Shows the choices of the active session, or the saved ones when there is none.
    /// </summary>
    private void ShowViewChoices()
    {
        _viewMenu.ShowLanguage(_settings.Language);
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

    /// <summary>
    /// Saves the language choice, sets the culture, writes every text again and tells the
    /// agents, so that the texts they send next follow the new language.
    /// </summary>
    private void OnLanguageChosen(string choice)
    {
        if (choice == _settings.Language)
        {
            return;
        }

        _settings.Language = choice;
        SaveViewChoices();
        UiLanguage.Apply(choice);
        ApplyTexts();
    }

    private void SaveViewChoices()
    {
        ClientLog.Info($"Display choices changed: {_settings}");
        _settings.Save(ClientSettings.DefaultPath);
        ShowViewChoices();
    }
}
