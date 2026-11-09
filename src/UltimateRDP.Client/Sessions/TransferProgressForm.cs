using System.Globalization;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// Small window that shows a transfer of large files from the drop to its end: the file
/// being sent, a progress bar, the rate and the time left, the destination, and a button
/// to cancel. Once the transfer ended it shows the result: after a success, the drop
/// result, then it closes by itself; after a failure or a cancel, the reason in red, until
/// the user closes it.
/// </summary>
/// <remarks>
/// <para>
/// The window is owned by the main form, so it stays above it without being topmost, and
/// it never takes the keyboard from the session: it is shown without activation and never
/// activated by the code. The session view that runs the transfer drives it, on the UI
/// thread.
/// </para>
/// <para>
/// The sizes are given at 96 DPI and scaled to the DPI of the monitor, so the window
/// follows it from one monitor to another. Every text is kept as a function of the
/// current language, so that
/// <see cref="ApplyTexts"/> can write it again after a change of language.
/// </para>
/// </remarks>
internal sealed class TransferProgressForm : Form
{
    private const int TickMilliseconds = 250;
    private const int AutoCloseMilliseconds = 3000;
    private const int BusyNoticeMilliseconds = 5000;
    private const int BarMaximum = 1000;

    private static readonly Color FailureColor = Color.FromArgb(192, 0, 0);
    private static readonly Color NoticeColor = Color.FromArgb(150, 80, 0);

    private readonly Label _fileLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _countLabel = new() { AutoSize = true, Anchor = AnchorStyles.Right, TextAlign = ContentAlignment.MiddleRight };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Fill, Maximum = BarMaximum, MarqueeAnimationSpeed = 30 };
    private readonly Label _statusLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.TopLeft };
    private readonly Label _destinationLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _noticeLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = NoticeColor };
    private readonly Button _button = new() { Anchor = AnchorStyles.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, Size = new Size(88, 27) };
    private readonly ToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = TickMilliseconds };
    private readonly LocalText _destination;

    private Stage _stage = Stage.Preparing;
    private Func<string> _fileText = () => string.Empty;
    private Func<string> _fileTip = () => string.Empty;
    private Func<string> _countText = () => string.Empty;
    private Func<string> _statusText = () => string.Empty;
    private Func<string> _doneText = () => string.Empty;
    private long _noticeUntil;
    private long? _closeAt;
    private Rectangle _area;
    private bool _movedByUser;

    /// <param name="dragId">The drag whose files are transferred.</param>
    /// <param name="host">The host name of the session, shown as the destination.</param>
    public TransferProgressForm(string dragId, string host)
    {
        DragId = dragId;
        _destination = LocalText.Of(nameof(Strings.TransferWindowDestination), host);

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
        table.SuspendLayout();
        SuspendLayout();
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // File, bar, two lines of status, destination, notice and button.
        foreach (var height in new[] { 24, 26, 42, 22, 36 })
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        }

        table.Controls.Add(_fileLabel, 0, 0);
        table.Controls.Add(_countLabel, 1, 0);
        table.Controls.Add(_bar, 0, 1);
        table.SetColumnSpan(_bar, 2);
        table.Controls.Add(_statusLabel, 0, 2);
        table.SetColumnSpan(_statusLabel, 2);
        table.Controls.Add(_destinationLabel, 0, 3);
        table.SetColumnSpan(_destinationLabel, 2);
        table.Controls.Add(_noticeLabel, 0, 4);
        table.Controls.Add(_button, 1, 4);

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 170);
        Padding = new Padding(10, 8, 10, 6);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = false;
        StartPosition = FormStartPosition.Manual;
        Controls.Add(table);
        table.ResumeLayout(false);
        ResumeLayout(false);
        PerformLayout();

        _button.Click += (_, _) => OnButtonClick();
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        Render();
    }

    /// <summary>Raised when the user asks to cancel the transfer.</summary>
    public event EventHandler? CancelRequested;

    private enum Stage
    {
        /// <summary>The files are being listed.</summary>
        Preparing,

        /// <summary>The files are on their way.</summary>
        Running,

        /// <summary>The agent has every file and drops the copies.</summary>
        Dropping,

        /// <summary>The drop result arrived; the window closes by itself.</summary>
        Done,

        /// <summary>The transfer failed or was cancelled; the window waits for the user.</summary>
        Failed,
    }

    /// <summary>The drag whose files are transferred.</summary>
    public string DragId { get; }

    /// <summary>True until the transfer ended, one way or the other.</summary>
    public bool IsRunning => _stage is Stage.Preparing or Stage.Running;

    protected override bool ShowWithoutActivation => true;

    /// <summary>The templates of the progress texts in the current user interface language.</summary>
    public static TransferTextFormats CurrentFormats() => new()
    {
        Progress = Strings.TransferWindowProgress,
        ProgressWithoutRate = Strings.TransferWindowProgressNoRate,
        Remaining = Strings.TransferWindowTimeLeft,
        Seconds = Strings.DurationSeconds,
        Minutes = Strings.DurationMinutes,
        MinutesSeconds = Strings.DurationMinutesSeconds,
        HoursMinutes = Strings.DurationHoursMinutes,
    };

    /// <summary>
    /// Shows the window, without activating it, centered over <paramref name="screenArea"/>,
    /// the screen area of the session, and owned by <paramref name="owner"/>.
    /// </summary>
    public void ShowOver(Form? owner, Rectangle screenArea)
    {
        _area = screenArea;
        if (owner is not null && Owner != owner)
        {
            Owner = owner;
        }

        Place();
        if (!Visible)
        {
            Show();
        }
    }

    /// <summary>The files are being listed: the bar runs without a value.</summary>
    /// <param name="firstItem">Name of the first dropped item.</param>
    /// <param name="itemCount">Number of dropped items.</param>
    public void ShowPreparing(string firstItem, int itemCount)
    {
        _stage = Stage.Preparing;
        _fileText = () => firstItem;
        _fileTip = () => firstItem;
        _countText = itemCount > 1 ? () => LocalText.Of(nameof(Strings.TransferWindowItemCount), itemCount).ToString() : () => string.Empty;
        _statusText = () => Strings.TransferWindowPreparing;
        SetBar(marquee: true, 0);
        Render();
    }

    /// <summary>"a.jpg, file 2 of 3" and "42 %, 2.6 MB/s, 1 min 40 s left".</summary>
    /// <param name="relativePath">Path of the current file in the drop.</param>
    /// <param name="fileNumber">Number of the current file, from 1.</param>
    /// <param name="fileCount">Number of files.</param>
    /// <param name="progress">The progress of the whole transfer.</param>
    public void ShowProgress(string relativePath, int fileNumber, int fileCount, TransferProgress progress)
    {
        if (!IsRunning)
        {
            return;
        }

        _stage = Stage.Running;
        var name = FileName(relativePath);
        _fileText = () => name;
        _fileTip = () => relativePath;
        _countText = () => LocalText.Of(nameof(Strings.TransferWindowFileOf), fileNumber, fileCount).ToString();
        _statusText = () => TransferProgressText.Describe(progress, CurrentFormats(), CultureInfo.CurrentCulture);
        SetBar(marquee: false, progress.TotalBytes == 0 ? BarMaximum : (int)(progress.DoneBytes * (double)BarMaximum / progress.TotalBytes));
        Render();
    }

    /// <summary>The agent has every file: "Transferred 3 file(s) in 3 min 13 s at 2.6 MB/s, dropping into the session...".</summary>
    public void ShowDropping(int fileCount, TimeSpan elapsed, double megabytesPerSecond)
    {
        if (!IsRunning)
        {
            return;
        }

        _stage = Stage.Dropping;
        _countText = () => LocalText.Of(nameof(Strings.TransferWindowFileOf), fileCount, fileCount).ToString();
        _statusText = () => Summary(Strings.TransferWindowDropping, fileCount, elapsed, megabytesPerSecond);
        _doneText = () => Summary(Strings.TransferWindowDone, fileCount, elapsed, megabytesPerSecond);
        SetBar(marquee: false, BarMaximum);
        Render();
    }

    /// <summary>
    /// The drop of the copies is over, with <paramref name="result"/>: the window shows it
    /// and closes three seconds later, once the mouse is not over it.
    /// </summary>
    public void ShowDropResult(LocalText result)
    {
        if (_stage != Stage.Dropping)
        {
            return;
        }

        _stage = Stage.Done;
        var done = _doneText;
        _statusText = () => done() + Environment.NewLine + result;
        _closeAt = Environment.TickCount64 + AutoCloseMilliseconds;
        Render();
    }

    /// <summary>The transfer failed or was cancelled: the reason in red, and a Close button.</summary>
    public void ShowFailure(LocalText reason)
    {
        if (_stage is Stage.Failed or Stage.Done)
        {
            return;
        }

        _stage = Stage.Failed;
        _statusText = reason.ToString;
        _noticeUntil = 0;
        _closeAt = null;
        SetBar(marquee: false, _bar.Style == ProgressBarStyle.Marquee ? 0 : _bar.Value);
        Render();
    }

    /// <summary>A new drag was refused during the transfer: says so for five seconds.</summary>
    public void ShowBusyNotice()
    {
        if (!IsRunning)
        {
            return;
        }

        var wasShown = Environment.TickCount64 < _noticeUntil;
        _noticeUntil = Environment.TickCount64 + BusyNoticeMilliseconds;
        if (!wasShown)
        {
            Render();
        }
    }

    /// <summary>Writes every text again in the current user interface language.</summary>
    public void ApplyTexts() => Render();

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // The size may have changed with the DPI of the monitor the window went to.
        Place();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BeginInvoke(Place);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);

        // The user moved the window: it stays where it was put.
        _movedByUser = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string FileName(string relativePath)
    {
        var index = relativePath.LastIndexOf(TransferPaths.Separator);
        return index < 0 ? relativePath : relativePath[(index + 1)..];
    }

    private static string Summary(string format, int fileCount, TimeSpan elapsed, double megabytesPerSecond) =>
        string.Format(
            CultureInfo.CurrentCulture,
            format,
            fileCount,
            TransferProgressText.Duration(elapsed, CurrentFormats(), CultureInfo.CurrentCulture),
            megabytesPerSecond);

    private void SetBar(bool marquee, int value)
    {
        var style = marquee ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (_bar.Style != style)
        {
            _bar.Style = style;
        }

        value = Math.Clamp(value, 0, BarMaximum);
        if (!marquee && _bar.Value != value)
        {
            _bar.Value = value;
        }
    }

    private void Render()
    {
        SetText(this, Strings.TransferWindowTitle);
        SetText(_fileLabel, _fileText());
        SetTip(_fileLabel, _fileTip());
        SetText(_countLabel, _countText());
        var status = _statusText();
        SetText(_statusLabel, status);
        SetTip(_statusLabel, status);
        var color = _stage == Stage.Failed ? FailureColor : SystemColors.ControlText;
        if (_statusLabel.ForeColor != color)
        {
            _statusLabel.ForeColor = color;
        }

        SetText(_destinationLabel, _destination.ToString());
        // Hidden while empty, so that a screen reader does not give it the name of another label.
        var notice = IsRunning && Environment.TickCount64 < _noticeUntil ? Strings.TransferWindowBusy : string.Empty;
        SetText(_noticeLabel, notice);
        // Visible reads false while the window is hidden, so the state is set every time.
        _noticeLabel.Visible = notice.Length > 0;

        SetText(_button, IsRunning ? Strings.TransferWindowCancel : Strings.TransferWindowClose);
        if (_bar.AccessibleName != Text)
        {
            _bar.AccessibleName = Text;
        }
    }

    private void SetTip(Control control, string text)
    {
        if (_toolTip.GetToolTip(control) != text)
        {
            _toolTip.SetToolTip(control, text);
        }
    }

    private static void SetText(Control control, string text)
    {
        if (control.Text != text)
        {
            control.Text = text;
        }
    }

    private void OnButtonClick()
    {
        if (IsRunning)
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Close();
        }
    }

    private void OnTick()
    {
        var now = Environment.TickCount64;
        if (_noticeLabel.Text.Length > 0 && now >= _noticeUntil)
        {
            Render();
        }

        // The result stays while the mouse is over the window, so that it can be read.
        if (_closeAt is { } closeAt && now >= closeAt && !Bounds.Contains(MousePosition))
        {
            _closeAt = null;
            Close();
        }
    }

    /// <summary>Centers the window over the session, within the screen, until the user moved it.</summary>
    private void Place()
    {
        if (_movedByUser || _area.IsEmpty || IsDisposed)
        {
            return;
        }

        var screen = Screen.FromRectangle(_area).WorkingArea;
        var x = _area.X + ((_area.Width - Width) / 2);
        var y = _area.Y + ((_area.Height - Height) / 2);
        x = Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - Width));
        y = Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - Height));
        var location = new Point(x, y);
        if (Location != location)
        {
            Location = location;
        }
    }
}
