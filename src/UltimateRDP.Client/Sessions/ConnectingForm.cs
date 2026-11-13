using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Resources;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// The small window "Connecting to &lt;program&gt;..." with Cancel, since 0.9.0, shown while a
/// RemoteApp program opened with the main window hidden connects, until its first window
/// appears. It sits in the bottom right corner of the monitor of the mouse, so that it does
/// not cover the credential prompt of Windows, has no taskbar button, and never takes the
/// keyboard.
/// </summary>
internal sealed class ConnectingForm : Form
{
    private static readonly Size LogicalClientSize = new(340, 92);

    private readonly Label _label = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
    private readonly Button _cancel = new() { Anchor = AnchorStyles.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, Size = new Size(88, 27) };
    private readonly string _name;

    public ConnectingForm(string name)
    {
        _name = name;
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        table.Controls.Add(_label, 0, 0);
        table.Controls.Add(_bar, 0, 1);
        table.Controls.Add(_cancel, 0, 2);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = LogicalClientSize;
        Padding = new Padding(10, 6, 10, 4);
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        Controls.Add(table);
        _cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        ApplyTexts();
    }

    /// <summary>Raised when the user clicks Cancel.</summary>
    public event EventHandler? CancelRequested;

    protected override bool ShowWithoutActivation => true;

    public void ApplyTexts()
    {
        Text = AppInfo.Name;
        _label.Text = LocalText.Of(nameof(Strings.ConnectingTo), _name).ToString();
        _cancel.Text = Strings.TransferWindowCancel;
    }

    protected override void OnLoad(EventArgs e)
    {
        ClientSize = LogicalToDeviceUnits(LogicalClientSize);
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        base.OnLoad(e);
    }
}
