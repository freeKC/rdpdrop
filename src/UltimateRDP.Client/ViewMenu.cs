using System.Globalization;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Resources;
using UltimateRDP.Client.Settings;
using UltimateRDP.Core.Display;
using UltimateRDP.Core.Localization;

namespace UltimateRDP.Client;

/// <summary>
/// The View menu: display mode, scale factor, full screen, monitors, Windows keys and
/// language. It only shows the state it is given and raises an event per choice; the
/// form decides what a choice does.
/// </summary>
internal sealed class ViewMenu
{
    private readonly ToolStripMenuItem _resizeItem = new();
    private readonly ToolStripMenuItem _scaleItem = new();
    private readonly ToolStripMenuItem _scaleFactorItem = new();
    private readonly ToolStripMenuItem _autoScaleItem = new();
    private readonly List<(int Factor, ToolStripMenuItem Item)> _factorItems = [];
    private readonly ToolStripMenuItem _fullScreenItem = new() { ShortcutKeyDisplayString = "F11" };
    private readonly ToolStripMenuItem _allMonitorsItem = new();
    private readonly ToolStripMenuItem _windowsKeysItem = new();
    private readonly ToolStripMenuItem[] _keyboardItems = [new(), new(), new()];
    private readonly ToolStripMenuItem _languageItem = new();
    private readonly ToolStripMenuItem _autoLanguageItem = new();
    private readonly List<(string Code, ToolStripMenuItem Item)> _languageItems = [];
    private int _autoFactor = 100;

    public ViewMenu()
    {
        _resizeItem.Click += (_, _) => DisplayModeChosen?.Invoke(this, DisplayMode.ResizeSession);
        _scaleItem.Click += (_, _) => DisplayModeChosen?.Invoke(this, DisplayMode.ScaleToWindow);

        _autoScaleItem.Click += (_, _) => ScaleFactorChosen?.Invoke(this, null);
        _scaleFactorItem.DropDownItems.Add(_autoScaleItem);
        _scaleFactorItem.DropDownItems.Add(new ToolStripSeparator());
        foreach (var factor in SessionDisplay.DesktopScaleFactors)
        {
            var item = new ToolStripMenuItem(Percent(factor));
            item.Click += (_, _) => ScaleFactorChosen?.Invoke(this, factor);
            _factorItems.Add((factor, item));
            _scaleFactorItem.DropDownItems.Add(item);
        }

        _fullScreenItem.Click += (_, _) => FullScreenClicked?.Invoke(this, EventArgs.Empty);
        _allMonitorsItem.Click += (_, _) => AllMonitorsChosen?.Invoke(this, !_allMonitorsItem.Checked);

        for (var mode = 0; mode < _keyboardItems.Length; mode++)
        {
            var chosen = mode;
            _keyboardItems[mode].Click += (_, _) => KeyboardHookModeChosen?.Invoke(this, chosen);
        }

        _windowsKeysItem.DropDownItems.AddRange(_keyboardItems);

        // Each language is named in its own language, so that anyone can find theirs.
        _autoLanguageItem.Click += (_, _) => LanguageChosen?.Invoke(this, UiLanguages.Automatic);
        _languageItem.DropDownItems.Add(_autoLanguageItem);
        _languageItem.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (code, nativeName) in UiLanguage.Languages)
        {
            var item = new ToolStripMenuItem(nativeName);
            item.Click += (_, _) => LanguageChosen?.Invoke(this, code);
            _languageItems.Add((code, item));
            _languageItem.DropDownItems.Add(item);
        }

        Root.DropDownItems.AddRange(
        [
            _resizeItem,
            _scaleItem,
            new ToolStripSeparator(),
            _scaleFactorItem,
            new ToolStripSeparator(),
            _fullScreenItem,
            _allMonitorsItem,
            new ToolStripSeparator(),
            _windowsKeysItem,
            new ToolStripSeparator(),
            _languageItem,
        ]);
        ApplyTexts();
    }

    public event EventHandler<DisplayMode>? DisplayModeChosen;

    /// <summary>Raised with a factor in percent, or null for Auto.</summary>
    public event EventHandler<int?>? ScaleFactorChosen;

    public event EventHandler? FullScreenClicked;

    /// <summary>Raised with the new value of "Use all monitors".</summary>
    public event EventHandler<bool>? AllMonitorsChosen;

    /// <summary>Raised with 0 (this computer), 1 (the session) or 2 (the session in full screen).</summary>
    public event EventHandler<int>? KeyboardHookModeChosen;

    /// <summary>Raised with "auto" or a two letter language code.</summary>
    public event EventHandler<string>? LanguageChosen;

    public ToolStripMenuItem Root { get; } = new();

    /// <summary>Writes every entry in the current user interface language.</summary>
    public void ApplyTexts()
    {
        Root.Text = Strings.MenuView;
        _resizeItem.Text = Strings.MenuResizeSession;
        _scaleItem.Text = Strings.MenuScaleToWindow;
        _scaleFactorItem.Text = Strings.MenuScaleFactor;
        _fullScreenItem.Text = Strings.MenuFullScreen;
        _allMonitorsItem.Text = Strings.MenuAllMonitors;
        _windowsKeysItem.Text = Strings.MenuWindowsKeys;
        _keyboardItems[0].Text = Strings.MenuKeysLocal;
        _keyboardItems[1].Text = Strings.MenuKeysRemote;
        _keyboardItems[2].Text = Strings.MenuKeysFullScreen;
        _languageItem.Text = Strings.MenuLanguage;
        _autoLanguageItem.Text = Strings.MenuLanguageAuto;
        _autoScaleItem.Text = LocalText.Of(nameof(Strings.MenuScaleAuto), Percent(_autoFactor)).ToString();
    }

    public void ShowDisplayMode(DisplayMode mode)
    {
        _resizeItem.Checked = mode == DisplayMode.ResizeSession;
        _scaleItem.Checked = mode == DisplayMode.ScaleToWindow;
    }

    /// <summary>
    /// Checks the chosen factor, or Auto when <paramref name="choice"/> is null.
    /// <paramref name="autoFactor"/> is shown next to Auto.
    /// </summary>
    public void ShowScaleFactor(int? choice, int autoFactor)
    {
        _autoFactor = autoFactor;
        _autoScaleItem.Text = LocalText.Of(nameof(Strings.MenuScaleAuto), Percent(autoFactor)).ToString();
        _autoScaleItem.Checked = choice is null;
        foreach (var (factor, item) in _factorItems)
        {
            item.Checked = choice == factor;
        }
    }

    public void ShowFullScreen(bool fullScreen, bool enabled)
    {
        _fullScreenItem.Checked = fullScreen;
        _fullScreenItem.Enabled = enabled;
    }

    public void ShowAllMonitors(bool useAllMonitors) => _allMonitorsItem.Checked = useAllMonitors;

    public void ShowKeyboardHookMode(int mode)
    {
        for (var i = 0; i < _keyboardItems.Length; i++)
        {
            _keyboardItems[i].Checked = i == mode;
        }
    }

    /// <summary>Checks "auto" or the chosen language.</summary>
    public void ShowLanguage(string choice)
    {
        _autoLanguageItem.Checked = choice == UiLanguages.Automatic;
        foreach (var (code, item) in _languageItems)
        {
            item.Checked = choice == code;
        }
    }

    private static string Percent(int factor) => factor.ToString(CultureInfo.InvariantCulture) + " %";
}
