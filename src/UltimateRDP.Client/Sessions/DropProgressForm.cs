using System.Globalization;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// Small window shown at every drop into the session, from the local drop to the result.
/// It has three stages: preparing or dropping, with a bar that runs without a value; for a
/// transfer of large files, the file being sent, the bar, the rate and the time left; and
/// the result. After a successful drop it shows the drop result, then closes by itself;
/// after a failure or a cancel it shows the reason in red, until the user closes it. It
/// also gives the destination, and a button to cancel a transfer.
/// </summary>
/// <remarks>
/// <para>
/// The window is owned by the main form, so it stays above it without being topmost, and
/// it never takes the keyboard from the session: it is shown without activation and never
/// activated by the code. The session view of the drop drives it, on the UI thread.
/// </para>
/// <para>
/// The sizes are given at 96 DPI and scaled to the DPI of the monitor, so the window
/// follows it from one monitor to another. Every text is kept as a function of the
/// current language, so that
/// <see cref="ApplyTexts"/> can write it again after a change of language.
/// </para>
/// </remarks>
internal sealed class DropProgressForm : Form
{
    private const int TickMilliseconds = 250;
    private const int AutoCloseMilliseconds = 3000;
    private const int BusyNoticeMilliseconds = 5000;
    private const int BarMaximum = 1000;

    /// <summary>Size of the client area at 96 DPI.</summary>
    private static readonly Size LogicalClientSize = new(460, 170);

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
    /// <summary>"Transferred 3 file(s) in ...", once a transfer succeeded; null for a drop without transfer.</summary>
    private Func<string>? _doneText;

    /// <summary>True while the files go through the direct connection: the status says so on its second line.</summary>
    private bool _direct;
    private LocalText? _fileWait;
    private long _noticeUntil;
    private long? _closeAt;
    private Rectangle _area;
    private bool _movedByUser;

    /// <param name="dragId">The drag whose files are dropped.</param>
    /// <param name="host">The host name of the session, shown as the destination.</param>
    public DropProgressForm(string dragId, string host)
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

        // The border first: a change of border keeps the outer size and changes the client size.
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = LogicalClientSize;
        Padding = new Padding(10, 8, 10, 6);
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
        /// <summary>The files of a transfer are being listed.</summary>
        Preparing,

        /// <summary>The files of a transfer are on their way.</summary>
        Running,

        /// <summary>The drop happens in the session, after a transfer or without one; its result is awaited.</summary>
        Dropping,

        /// <summary>The drop result arrived; the window closes by itself.</summary>
        Done,

        /// <summary>The transfer or the drop failed, or was cancelled; the window waits for the user.</summary>
        Failed,
    }

    /// <summary>The drag whose files are dropped.</summary>
    public string DragId { get; }

    /// <summary>True while a transfer runs, until it ended one way or the other.</summary>
    public bool IsRunning => _stage is Stage.Preparing or Stage.Running;

    /// <summary>True while the result of the drop is awaited.</summary>
    public bool IsWaitingForDrop => _stage == Stage.Dropping;

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
    /// the screen area of the session, and owned by <paramref name="owner"/>. A RemoteApp
    /// drop has no owner, since the main window may be minimized, and stays on top of the
    /// windows of the program with <paramref name="topMost"/>.
    /// </summary>
    public void ShowOver(Form? owner, Rectangle screenArea, bool topMost = false)
    {
        _area = screenArea;
        if (owner is not null && Owner != owner)
        {
            Owner = owner;
        }

        if (TopMost != topMost)
        {
            TopMost = topMost;
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
        SetItems(firstItem, itemCount);
        _statusText = () => Strings.TransferWindowPreparing;
        SetBar(marquee: true, 0);
        Render();
    }

    /// <summary>
    /// A drop without transfer went out: the remote application reads the files through
    /// drive redirection. The bar runs without a value until the result.
    /// </summary>
    /// <param name="firstItem">Name of the first dropped item.</param>
    /// <param name="itemCount">Number of dropped items.</param>
    public void ShowDropping(string firstItem, int itemCount)
    {
        _stage = Stage.Dropping;
        _doneText = null;
        SetItems(firstItem, itemCount);
        _statusText = () => Strings.DropWindowDropping;
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

    /// <summary>
    /// The agent has every file and drops the copies: "Transferred 3 file(s) in 3 min 13 s
    /// at 2.6 MB/s, dropping into the session...". The bar runs without a value until the result.
    /// </summary>
    public void ShowTransferred(int fileCount, TimeSpan elapsed, double megabytesPerSecond)
    {
        if (!IsRunning)
        {
            return;
        }

        _stage = Stage.Dropping;
        _countText = () => LocalText.Of(nameof(Strings.TransferWindowFileOf), fileCount, fileCount).ToString();
        _statusText = () => Summary(Strings.TransferWindowDropping, fileCount, elapsed, megabytesPerSecond);
        _doneText = () => Summary(Strings.TransferWindowDone, fileCount, elapsed, megabytesPerSecond);
        SetBar(marquee: true, 0);
        Render();
    }

    /// <summary>
    /// The drop is over, with <paramref name="result"/>. When it worked, or when nothing
    /// under the cursor took the files, the window shows it, after the summary of the
    /// transfer if there was one, and closes three seconds later, once the mouse is not
    /// over it. When it <paramref name="failed"/>, the result is shown as a failure.
    /// </summary>
    public void ShowDropResult(LocalText result, bool failed)
    {
        if (_stage != Stage.Dropping)
        {
            return;
        }

        if (failed)
        {
            ShowFailure(result);
            return;
        }

        _stage = Stage.Done;
        var done = _doneText;
        _statusText = done is null ? result.ToString : () => done() + Environment.NewLine + result;
        _closeAt = Environment.TickCount64 + AutoCloseMilliseconds;
        SetBar(marquee: false, BarMaximum);
        Render();
    }

    /// <summary>The transfer or the drop failed, or was cancelled: the reason in red, and a Close button.</summary>
    public void ShowFailure(LocalText reason)
    {
        if (_stage is Stage.Failed or Stage.Done)
        {
            return;
        }

        // After a transfer that succeeded, the bar stays full: only the drop failed.
        var value = _doneText is not null ? BarMaximum : _bar.Style == ProgressBarStyle.Marquee ? 0 : _bar.Value;
        _stage = Stage.Failed;
        _statusText = reason.ToString;
        _noticeUntil = 0;
        _closeAt = null;
        SetBar(marquee: false, value);
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

    /// <summary>
    /// The transfer goes through the direct connection, or no longer does: while it runs,
    /// the status adds "Direct connection" on its second line.
    /// </summary>
    public void ShowDirect(bool direct)
    {
        if (_direct != direct)
        {
            _direct = direct;
            Render();
        }
    }

    /// <summary>
    /// A file takes long to open: while the transfer runs, the status says so instead of the
    /// stage, until null ends it.
    /// </summary>
    public void ShowFileWait(LocalText? text)
    {
        _fileWait = text;
        Render();
    }

    /// <summary>Writes every text again in the current user interface language.</summary>
    public void ApplyTexts() => Render();

    protected override void OnLoad(EventArgs e)
    {
        // The window now exists on its monitor and knows its DPI.
        FitToDpi();
        base.OnLoad(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // The size may have changed with the DPI of the monitor the window went to.
        Place();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BeginInvoke(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            FitToDpi();

            // Taken to another monitor than the session, for example with Win+Shift+Arrow:
            // it stays there, as after a move with the mouse. Moving it back from here
            // would start a second change of DPI inside the first one.
            if (!_area.IsEmpty && Screen.FromControl(this).DeviceName != Screen.FromRectangle(_area).DeviceName)
            {
                _movedByUser = true;
            }

            Place();
        });
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

    /// <summary>The first dropped item, and "2 item(s)" when there are more.</summary>
    private void SetItems(string firstItem, int itemCount)
    {
        _fileText = () => firstItem;
        _fileTip = () => firstItem;
        _countText = itemCount > 1 ? () => LocalText.Of(nameof(Strings.TransferWindowItemCount), itemCount).ToString() : () => string.Empty;
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
        var status = _fileWait is { } wait && IsRunning ? wait.ToString() : _statusText();
        if (_direct && IsRunning)
        {
            status += Environment.NewLine + Strings.TransferDirect;
        }

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

    /// <summary>
    /// Gives the client area its size at the DPI of the monitor. The auto scaling of
    /// Windows Forms scales the outer size, title bar included, so the client area
    /// would grow at each scaling.
    /// </summary>
    private void FitToDpi()
    {
        var size = LogicalToDeviceUnits(LogicalClientSize);
        if (ClientSize != size)
        {
            ClientSize = size;
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
