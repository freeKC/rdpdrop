using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Rdp;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Client.Loader;

/// <summary>
/// The loader of one start, since 0.9.3: a small borderless window with the mark of the
/// application, what is opening, a moving bar and the step of the start, shown at once when the
/// process starts with a .rdp file, and for each session that opens later. It replaces the
/// connecting window of 0.9.0.
/// </summary>
/// <remarks>
/// <para>
/// It never takes the focus: it is shown without activation, has WS_EX_NOACTIVATE, answers
/// WM_MOUSEACTIVATE with MA_NOACTIVATE, and is not topmost. It has no taskbar button. Every 250 ms
/// it looks for a window that waits for an answer (<see cref="LoaderRules.IsAnswerAwaited"/>):
/// while one shows, such as the certificate question of the control or the credential prompt of
/// Windows, it hides, and it comes back once the dialog is gone if the start still runs; the log
/// says both.
/// </para>
/// <para>
/// It sits in the middle of the monitor of the mouse, or over the main window when that one is on
/// a monitor; several loaders stack one below the other. Its size follows the DPI of its monitor,
/// its colors the app theme of Windows. On Windows 11 the corners are rounded by Windows; the
/// shadow is the drop shadow of the window class.
/// </para>
/// </remarks>
internal sealed class LoaderForm : Form
{
    private const int FrameMilliseconds = 33;
    private const int DialogCheckMilliseconds = 250;
    private const int StackGap = 12;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int CsDropShadow = 0x00020000;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    private static readonly List<LoaderForm> Open = [];

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = FrameMilliseconds };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly LoaderTheme _theme = LoaderTheme.FromWindows();
    private readonly string _label;
    private LoaderFonts _fonts;
    private Form? _over;
    private Rectangle _area;
    private long _nextDialogCheck;
    private bool _hiddenForDialog;
    private bool _cancelHot;
    private bool _canCancel;
    private bool _finished;

    /// <param name="label">What the log calls this loader, such as "the start" or a host.</param>
    public LoaderForm(string label)
    {
        _label = label;
        Progress = new LoaderProgress(0);
        _fonts = new LoaderFonts(1f);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = false;
        Text = AppInfo.Name;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _timer.Tick += (_, _) => OnFrame();
    }

    /// <summary>Raised when the user clicks Cancel, once a session can be cancelled.</summary>
    public event EventHandler? CancelRequested;

    /// <summary>The steps of the start.</summary>
    public LoaderProgress Progress { get; }

    /// <summary>True when Cancel shows: a session is attached.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool CanCancel
    {
        get => _canCancel;
        set
        {
            if (_canCancel != value)
            {
                _canCancel = value;
                Invalidate();
            }
        }
    }

    /// <summary>The milliseconds since the loader was created.</summary>
    public long ElapsedMilliseconds => _clock.ElapsedMilliseconds;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow;
            parameters.ClassStyle |= CsDropShadow;
            return parameters;
        }
    }

    /// <summary>
    /// Shows the loader on the monitor of the mouse, or over <paramref name="over"/> when that
    /// window is on a monitor, and paints it at once, before the caller goes on with the start.
    /// </summary>
    public void ShowNow(Form? over = null)
    {
        _over = over;
        _area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = LoaderPainter.LogicalSize;
        Location = new Point(_area.X + ((_area.Width - Width) / 2), _area.Y + ((_area.Height - Height) / 2));
        _ = Handle;
        FitToDpi();
        RoundCorners();
        lock (Open)
        {
            Open.Add(this);
        }

        PlaceAll();
        Show();
        Update();
        _timer.Start();
        ClientLog.Info(string.Create(CultureInfo.InvariantCulture, $"Loader of {_label} shown at {Bounds} on a monitor of {DeviceDpi} DPI, {(_theme.IsDark ? "dark" : "light")} theme."));
    }

    /// <summary>
    /// Puts the loader over the main window, which became visible on a monitor, so that it stays
    /// above it; null puts it back in the middle of the monitor of the mouse.
    /// </summary>
    public void PlaceOver(Form? over)
    {
        if (_finished)
        {
            return;
        }

        _over = over;
        if (Owner != over)
        {
            Owner = over;
        }

        PlaceAll();
    }

    /// <summary>The .rdp file was read: what is opening shows under the name of the application.</summary>
    public void FileRead(string server, string? program)
    {
        Progress.FileRead(server, program);
        Invalidate();
    }

    /// <summary>An event of the start; the loader closes when the start is over.</summary>
    public void On(LoaderEvent loaderEvent)
    {
        if (_finished || !Progress.On(loaderEvent))
        {
            return;
        }

        ClientLog.Info(string.Create(CultureInfo.InvariantCulture, $"Loader of {_label}: {Progress.Step} after {ElapsedMilliseconds} ms ({loaderEvent})."));
        if (Progress.IsClosed)
        {
            Finish(Progress.CloseReason ?? "the start is over");
            return;
        }

        Invalidate();
    }

    /// <summary>Closes the loader now, for <paramref name="reason"/>, in English.</summary>
    public void Finish(string reason)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        Progress.On(LoaderEvent.Ended);
        _timer.Stop();
        ClientLog.Info(string.Create(CultureInfo.InvariantCulture, $"Loader of {_label} closed after {ElapsedMilliseconds} ms: {reason}."));
        lock (Open)
        {
            Open.Remove(this);
        }

        if (!IsDisposed)
        {
            Close();
            Dispose();
        }

        PlaceAll();
    }

    /// <summary>What the loader shows now, in the language of the user interface.</summary>
    public LoaderVisual Visual() => Describe(Progress, CanCancel, _cancelHot, LoaderPainter.PhaseAt(ElapsedMilliseconds), ElapsedMilliseconds);

    /// <summary>The texts of <paramref name="progress"/> in the language of the user interface.</summary>
    public static LoaderVisual Describe(LoaderProgress progress, bool canCancel, bool cancelHot, double phase, long elapsedMilliseconds)
    {
        var step = progress.StepTextArgument is { } argument
            ? LocalText.Of(progress.StepTextKey, argument).ToString()
            : LocalText.Of(progress.StepTextKey).ToString();
        return new LoaderVisual(
            progress.Title,
            step,
            progress.ShowsSlowNotice(elapsedMilliseconds) ? Strings.LoaderSlow : null,
            canCancel ? Strings.LoaderCancel : null,
            cancelHot,
            phase);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate)
        {
            m.Result = MaNoActivate;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        LoaderPainter.Paint(e.Graphics, ClientSize, _theme, Visual(), _fonts);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitToDpi();
        PlaceAll();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hot = CanCancel && LoaderPainter.CancelBounds(ClientSize, _fonts, Strings.LoaderCancel).Contains(e.Location);
        if (hot != _cancelHot)
        {
            _cancelHot = hot;
            Cursor = hot ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_cancelHot)
        {
            _cancelHot = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && CanCancel && LoaderPainter.CancelBounds(ClientSize, _fonts, Strings.LoaderCancel).Contains(e.Location))
        {
            ClientLog.Info($"Loader of {_label}: Cancel.");
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _fonts.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Places every open loader: over its main window, or in the middle of its monitor, one below the other.</summary>
    private static void PlaceAll()
    {
        LoaderForm[] loaders;
        lock (Open)
        {
            loaders = [.. Open];
        }

        var placed = new Dictionary<Rectangle, int>();
        foreach (var loader in loaders.Where(loader => !loader.IsDisposed && loader.IsHandleCreated))
        {
            var area = loader._over is { IsDisposed: false, Visible: true } over && Screen.AllScreens.Any(screen => screen.Bounds.IntersectsWith(over.Bounds))
                ? over.Bounds
                : loader._area;
            placed.TryGetValue(area, out var index);
            placed[area] = index + 1;
            var x = area.X + ((area.Width - loader.Width) / 2);
            var y = area.Y + ((area.Height - loader.Height) / 2) + (index * (loader.Height + loader.LogicalToDeviceUnits(StackGap)));
            var location = new Point(x, y);
            if (loader.Location != location)
            {
                loader.Location = location;
            }
        }
    }

    private void FitToDpi()
    {
        var scale = DeviceDpi / 96f;
        if (Math.Abs(_fonts.Scale - scale) > 0.001f)
        {
            var old = _fonts;
            _fonts = new LoaderFonts(scale);
            old.Dispose();
        }

        var size = new Size((int)Math.Round(LoaderPainter.LogicalSize.Width * scale), (int)Math.Round(LoaderPainter.LogicalSize.Height * scale));
        if (ClientSize != size)
        {
            ClientSize = size;
        }
    }

    /// <summary>Windows 11 rounds the corners of the window; earlier versions keep them square, which the border suits.</summary>
    private void RoundCorners()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    /// <summary>A frame of the bar, and every 250 ms the dialogs and the notice of a slow start.</summary>
    private void OnFrame()
    {
        if (_finished || IsDisposed)
        {
            return;
        }

        var now = ElapsedMilliseconds;
        if (now >= _nextDialogCheck)
        {
            _nextDialogCheck = now + DialogCheckMilliseconds;
            CheckDialogs();
            if (Progress.ShowsSlowNotice(now))
            {
                Invalidate();
            }
        }

        if (!_hiddenForDialog)
        {
            Invalidate(LoaderPainter.BarBounds(ClientSize, _fonts.Scale));
        }
    }

    /// <summary>Hides the loader while a window waits for an answer of the user, and shows it again afterwards.</summary>
    private void CheckDialogs()
    {
        var dialog = AwaitedDialog();
        if (dialog is { } found && !_hiddenForDialog)
        {
            _hiddenForDialog = true;
            NativeMethods.ShowWindow(Handle, NativeMethods.SwHide);
            ClientLog.Info($"Loader of {_label} hidden after {ElapsedMilliseconds} ms: a window waits for an answer ({found}).");
        }
        else if (dialog is null && _hiddenForDialog)
        {
            _hiddenForDialog = false;
            NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
            ClientLog.Info($"Loader of {_label} shown again after {ElapsedMilliseconds} ms: no window waits for an answer any more.");
        }
    }

    /// <summary>The first visible top level window that waits for an answer, described for the log, or null.</summary>
    private string? AwaitedDialog()
    {
        string? found = null;
        var self = (uint)Environment.ProcessId;
        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                if (hwnd == Handle || !NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                var className = NativeMethods.ClassName(hwnd);
                NativeMethods.GetWindowThreadProcessId(hwnd, out var process);
                if (!LoaderRules.IsAnswerAwaited(className, process == self))
                {
                    return true;
                }

                found = string.Create(CultureInfo.InvariantCulture, $"class {className}, process {process}{(process == self ? ", this one" : string.Empty)}, title \"{NativeMethods.WindowText(hwnd)}\"");
                return false;
            },
            IntPtr.Zero);
        return found;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
