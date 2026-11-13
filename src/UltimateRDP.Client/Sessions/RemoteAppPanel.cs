using UltimateRDP.Client.Resources;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// What a RemoteApp tab shows instead of the picture of a session: the name of the program,
/// where its windows are and how many are open, the state of the connection and of the agent,
/// the result of the last drop, and a button that disconnects. The control stays below it,
/// docked and alive, its window hidden since 0.9.0.
/// </summary>
internal sealed class RemoteAppPanel : Panel
{
    private readonly Label _title = NewLabel();
    private readonly Label _hint = NewLabel();
    private readonly Label _windows = NewLabel();
    private readonly Button _disconnect = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left, Margin = new Padding(0, 12, 0, 0) };
    private readonly Label _connection = NewLabel();
    private readonly Label _agent = NewLabel();
    private readonly Label _drop = NewLabel();
    private Font? _titleFont;

    public RemoteAppPanel()
    {
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
        Padding = new Padding(24);
        AllowDrop = false;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = SystemColors.Window,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        foreach (var control in new Control[] { _title, _hint, _windows, _connection, _agent, _drop, _disconnect })
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(control);
        }

        _hint.Margin = new Padding(0, 6, 0, 6);
        _windows.Margin = new Padding(0, 0, 0, 18);
        _disconnect.Click += (_, _) => DisconnectRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(table);
    }

    /// <summary>Raised when the user clicks Disconnect.</summary>
    public event EventHandler? DisconnectRequested;

    /// <summary>Shows the texts, already in the language of the user interface.</summary>
    public void ShowState(string programName, string hint, string windows, string connection, string agent, string drop, bool canDisconnect)
    {
        SetText(_title, programName);
        SetText(_hint, hint);
        SetText(_windows, windows);
        SetText(_connection, connection);
        SetText(_agent, agent);
        SetText(_drop, drop);
        SetText(_disconnect, Strings.RemoteAppDisconnect);
        if (_disconnect.Enabled != canDisconnect)
        {
            _disconnect.Enabled = canDisconnect;
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateTitleFont();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateTitleFont();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont?.Dispose();
            _titleFont = null;
        }

        base.Dispose(disposing);
    }

    private void UpdateTitleFont()
    {
        var previous = _titleFont;
        _titleFont = new Font(Font.FontFamily, Font.SizeInPoints * 1.6F, FontStyle.Bold);
        _title.Font = _titleFont;
        previous?.Dispose();
    }

    private static Label NewLabel() => new()
    {
        AutoSize = true,
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 3, 0, 3),
        UseMnemonic = false,
    };

    private static void SetText(Control control, string text)
    {
        if (control.Text != text)
        {
            control.Text = text;
        }
    }
}
