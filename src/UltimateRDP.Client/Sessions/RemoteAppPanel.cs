namespace UltimateRDP.Client.Sessions;

/// <summary>
/// What a RemoteApp tab shows instead of the picture of a session: the name of the program,
/// where its windows are, the state of the connection and of the agent, and the result of
/// the last drop. The control stays below it, docked and alive; in RemoteApp mode it shows
/// nothing.
/// </summary>
internal sealed class RemoteAppPanel : Panel
{
    private readonly Label _title = NewLabel();
    private readonly Label _hint = NewLabel();
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
        foreach (var label in new[] { _title, _hint, _connection, _agent, _drop })
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(label);
        }

        _hint.Margin = new Padding(0, 6, 0, 18);
        Controls.Add(table);
    }

    /// <summary>Shows the texts, already in the language of the user interface.</summary>
    public void ShowState(string programName, string hint, string connection, string agent, string drop)
    {
        SetText(_title, programName);
        SetText(_hint, hint);
        SetText(_connection, connection);
        SetText(_agent, agent);
        SetText(_drop, drop);
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

    private static void SetText(Label label, string text)
    {
        if (label.Text != text)
        {
            label.Text = text;
        }
    }
}
