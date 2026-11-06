using System.Globalization;
using RdpDrop.Client.Settings;
using RdpDrop.Core.Display;

namespace RdpDrop.Client;

/// <summary>
/// The View menu: display mode, scale factor, full screen, monitors and Windows keys.
/// It only shows the state it is given and raises an event per choice; the form decides
/// what a choice does.
/// </summary>
internal sealed class ViewMenu
{
    private readonly ToolStripMenuItem _resizeItem = new("&Resize session to window");
    private readonly ToolStripMenuItem _scaleItem = new("&Scale to window");
    private readonly ToolStripMenuItem _scaleFactorItem = new("Scale &factor");
    private readonly ToolStripMenuItem _autoScaleItem = new("&Auto");
    private readonly List<(int Factor, ToolStripMenuItem Item)> _factorItems = [];
    private readonly ToolStripMenuItem _fullScreenItem = new("F&ull screen") { ShortcutKeyDisplayString = "F11" };
    private readonly ToolStripMenuItem _allMonitorsItem = new("Use all &monitors");
    private readonly ToolStripMenuItem _windowsKeysItem = new("&Windows keys");
    private readonly ToolStripMenuItem[] _keyboardItems =
    [
        new("On this &computer"),
        new("In the &session"),
        new("In the session when &full screen"),
    ];

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
        ]);
    }

    public event EventHandler<DisplayMode>? DisplayModeChosen;

    /// <summary>Raised with a factor in percent, or null for Auto.</summary>
    public event EventHandler<int?>? ScaleFactorChosen;

    public event EventHandler? FullScreenClicked;

    /// <summary>Raised with the new value of "Use all monitors".</summary>
    public event EventHandler<bool>? AllMonitorsChosen;

    /// <summary>Raised with 0 (this computer), 1 (the session) or 2 (the session in full screen).</summary>
    public event EventHandler<int>? KeyboardHookModeChosen;

    public ToolStripMenuItem Root { get; } = new("&View");

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
        _autoScaleItem.Text = $"&Auto ({Percent(autoFactor)})";
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

    private static string Percent(int factor) => factor.ToString(CultureInfo.InvariantCulture) + " %";
}
