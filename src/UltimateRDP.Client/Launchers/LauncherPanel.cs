using System.Globalization;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Launchers;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Client.Launchers;

/// <summary>
/// The content of the launcher generator, since 0.10.0: the fields, the preview of the .bat
/// and the buttons. The first view shows the name, the server, the port, the user, the kind
/// and the program; "Advanced options" shows the rest. Every change of a field writes the
/// preview again, or says which field is wrong; the buttons raise events that
/// <see cref="LauncherForm"/> handles.
/// </summary>
/// <remarks>
/// The sizes are given at 96 DPI and multiplied by the scale given at creation, with a font
/// in pixels: the dialog creates the panel again when it moves to a monitor with another
/// DPI, and the previews draw it at 100 % and 200 % on any computer.
/// </remarks>
internal sealed class LauncherPanel : UserControl
{
    /// <summary>Width of the panel at 96 DPI.</summary>
    private const int LogicalWidth = 700;

    /// <summary>Lines of the preview.</summary>
    private const int PreviewLines = 9;

    private static readonly Color ErrorColor = Color.FromArgb(192, 0, 0);
    private static readonly Color HintColor = Color.FromArgb(96, 96, 96);

    /// <summary>Marks the hints of the grid.</summary>
    private static readonly object HintTag = new();

    private readonly float _scale;
    private readonly string _runningExe;
    private readonly TableLayoutPanel _grid = new() { ColumnCount = 4, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
    private readonly List<Control> _advanced = [];
    private readonly List<int> _advancedRows = [];
    private readonly ToolTip _toolTip = new();

    private readonly TextBox _name = new();
    private readonly TextBox _server = new();
    private readonly TextBox _port = new();
    private readonly TextBox _user = new();
    private readonly RadioButton _kindDesktop = new() { AutoSize = true, Checked = true };
    private readonly RadioButton _kindRemoteApp = new() { AutoSize = true };
    private readonly TextBox _program = new();
    private readonly CheckBox _showAdvanced = new() { AutoSize = true };
    private readonly TextBox _domain = new();
    private readonly TextBox _gateway = new();
    private readonly TextBox _displayName = new();
    private readonly TextBox _arguments = new();
    private readonly CheckBox _askPassword = new() { AutoSize = true };
    private readonly CheckBox _trusted = new() { AutoSize = true };
    private readonly ComboBox _window = NewCombo();
    private readonly ComboBox _multimon = NewCombo();
    private readonly ComboBox _scaleChoice = NewCombo();
    private readonly ComboBox _windowsKeys = NewCombo();
    private readonly ComboBox _audio = NewCombo();
    private readonly ComboBox _clipboard = NewCombo();
    private readonly ComboBox _drives = NewCombo();
    private readonly ComboBox _printers = NewCombo();
    private readonly RadioButton _exeRunning = new() { AutoSize = false, AutoEllipsis = true, Checked = true };
    private readonly RadioButton _exeNext = new() { AutoSize = true };
    private readonly RadioButton _exeTyped = new() { AutoSize = true };
    private readonly TextBox _typedExe = new();
    private readonly RadioButton _outputBat = new() { AutoSize = true, Checked = true };
    private readonly RadioButton _outputBatRdp = new() { AutoSize = true };
    private readonly TextBox _preview = new() { Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Button _fillSession = NewButton();
    private readonly Button _fillFile = NewButton();
    private readonly Button _test = NewButton();
    private readonly Button _copy = NewButton();
    private readonly Button _save = NewButton();
    private readonly Button _close = NewButton();

    private int _rowHeight;
    private bool _loading;
    private LauncherSpec? _spec;

    /// <param name="scale">The DPI of the monitor divided by 96.</param>
    /// <param name="font">The font of the dialog, already at <paramref name="scale"/>.</param>
    /// <param name="monoFont">The font of the preview, already at <paramref name="scale"/>.</param>
    /// <param name="runningExe">The path of the running UltimateRDP.exe.</param>
    /// <param name="canFillFromSession">True when a tab is open.</param>
    public LauncherPanel(float scale, Font font, Font monoFont, string runningExe, bool canFillFromSession)
    {
        _scale = scale;
        _runningExe = runningExe;
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Font = font;
        Padding = Scaled(new Padding(12, 10, 12, 10));
        _preview.Font = monoFont;
        _fillSession.Enabled = canFillFromSession;

        using (var probe = new TextBox { Font = font })
        {
            _rowHeight = probe.PreferredHeight + Px(8);
        }

        BuildGrid();
        var width = Px(LogicalWidth);
        FixHintWidths(width - _grid.Margin.Horizontal);
        var previewHeight = (TextRenderer.MeasureText("X", monoFont).Height * PreviewLines) + Px(8);
        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(Padding.Left, Padding.Top),
            Margin = Padding.Empty,
            MinimumSize = new Size(width, 0),
            MaximumSize = new Size(width, 0),
        };

        // The panel takes the size of its content, which the advanced options change; the
        // dialog takes the size of the panel.
        root.SizeChanged += (_, _) => Size = new Size(root.Width + Padding.Horizontal, root.Height + Padding.Vertical);
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var intro = new Label { AutoSize = true, MaximumSize = new Size(width, 0), Text = Strings.LauncherIntro, Margin = Scaled(new Padding(0, 0, 0, 8)) };
        var previewLabel = new Label { AutoSize = true, Text = Strings.LauncherPreview, Margin = Scaled(new Padding(0, 10, 0, 2)) };
        var previewBox = new Panel { Height = previewHeight, Dock = DockStyle.Top, Margin = Padding.Empty };
        previewBox.Controls.Add(_preview);
        _status.MaximumSize = new Size(width, 0);
        _status.Margin = Scaled(new Padding(0, 4, 0, 4));
        var fill = Flow(_fillSession, _fillFile);
        fill.Margin = Scaled(new Padding(0, 0, 0, 8));
        root.Controls.Add(intro);
        root.Controls.Add(fill);
        root.Controls.Add(_grid);
        root.Controls.Add(previewLabel);
        root.Controls.Add(previewBox);
        root.Controls.Add(_status);
        root.Controls.Add(BuildButtons(width));
        Controls.Add(root);

        ApplyTexts();
        WireEvents();
        ShowAdvanced(false);
        ResumeLayout(true);
        UpdatePreview();
    }

    /// <summary>"Fill from the current session".</summary>
    public event EventHandler? FillFromSessionClicked;

    /// <summary>"Fill from a .rdp file...".</summary>
    public event EventHandler? FillFromFileClicked;

    /// <summary>"Test".</summary>
    public event EventHandler? TestClicked;

    /// <summary>"Copy".</summary>
    public event EventHandler? CopyClicked;

    /// <summary>"Save as...".</summary>
    public event EventHandler? SaveClicked;

    /// <summary>"Close".</summary>
    public event EventHandler? CloseClicked;

    /// <summary>The fields as typed.</summary>
    public LauncherDraft Draft => new()
    {
        Name = _name.Text,
        Server = _server.Text,
        Port = _port.Text,
        User = _user.Text,
        Domain = _domain.Text,
        RemoteApp = _kindRemoteApp.Checked,
        Program = _program.Text,
        DisplayName = _displayName.Text,
        Arguments = _arguments.Text,
        Gateway = _gateway.Text,
        AskPassword = _askPassword.Checked,
        Trusted = _trusted.Checked,
        Window = Selected<LaunchWindow>(_window),
        Multimon = Selected<bool?>(_multimon),
        Scale = Selected<int?>(_scaleChoice),
        WindowsKeys = Selected<WindowsKeysTarget?>(_windowsKeys),
        Clipboard = Selected<bool?>(_clipboard),
        Drives = Selected<bool?>(_drives),
        Printers = Selected<bool?>(_printers),
        Audio = Selected<AudioTarget?>(_audio),
        ExeKind = _exeNext.Checked ? LauncherExeKind.NextToLauncher : _exeTyped.Checked ? LauncherExeKind.WithVariables : LauncherExeKind.FullPath,
        TypedExe = _typedExe.Text,
        WithRdpFile = _outputBatRdp.Checked,
    };

    /// <summary>The launcher of the fields, or null while a field is wrong.</summary>
    public LauncherSpec? Spec => _spec;

    /// <summary>True while the advanced options show.</summary>
    public bool AdvancedShown => _showAdvanced.Checked;

    /// <summary>The button that closes the dialog, for Escape.</summary>
    public IButtonControl CloseButton => _close;

    /// <summary>Puts <paramref name="draft"/> in the fields.</summary>
    public void SetDraft(LauncherDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _loading = true;
        try
        {
            _name.Text = draft.Name;
            _server.Text = draft.Server;
            _port.Text = draft.Port;
            _user.Text = draft.User;
            _domain.Text = draft.Domain;
            _kindRemoteApp.Checked = draft.RemoteApp;
            _kindDesktop.Checked = !draft.RemoteApp;
            _program.Text = draft.Program;
            _displayName.Text = draft.DisplayName;
            _arguments.Text = draft.Arguments;
            _gateway.Text = draft.Gateway;
            _askPassword.Checked = draft.AskPassword;
            _trusted.Checked = draft.Trusted;
            Select(_window, draft.Window);
            Select(_multimon, draft.Multimon);
            Select(_scaleChoice, draft.Scale);
            Select(_windowsKeys, draft.WindowsKeys);
            Select(_clipboard, draft.Clipboard);
            Select(_drives, draft.Drives);
            Select(_printers, draft.Printers);
            Select(_audio, draft.Audio);
            _exeRunning.Checked = draft.ExeKind == LauncherExeKind.FullPath;
            _exeNext.Checked = draft.ExeKind == LauncherExeKind.NextToLauncher;
            _exeTyped.Checked = draft.ExeKind == LauncherExeKind.WithVariables;
            _typedExe.Text = draft.TypedExe;
            _outputBatRdp.Checked = draft.WithRdpFile;
            _outputBat.Checked = !draft.WithRdpFile;
        }
        finally
        {
            _loading = false;
        }

        UpdatePreview();
    }

    /// <summary>Shows or hides the advanced options.</summary>
    public void ShowAdvanced(bool show)
    {
        _showAdvanced.Checked = show;
        SuspendLayout();
        foreach (var control in _advanced)
        {
            control.Visible = show;
        }

        foreach (var row in _advancedRows)
        {
            _grid.RowStyles[row].Height = show ? _rowHeight : 0;
        }

        ResumeLayout(true);
    }

    /// <summary>Shows a result under the preview, in red for an error; the next change of a field takes it away.</summary>
    public void ShowStatus(string text, bool error)
    {
        _status.ForeColor = error ? ErrorColor : SystemColors.ControlText;
        _status.Text = text;
    }

    /// <summary>The lines of the comment at the top of the launcher, in the language of the user.</summary>
    public static IReadOnlyList<string> Comments(LauncherDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var server = draft.Server.Trim();
        var lines = new List<string>
        {
            $"{AppInfo.Brand} {AppInfo.Version}",
            draft.RemoteApp
                ? LocalText.Of(nameof(Strings.LauncherCommentRemoteApp), draft.DefaultName, server).ToString()
                : LocalText.Of(nameof(Strings.LauncherCommentDesktop), server).ToString(),
            Strings.LauncherCommentPassword,
        };
        if (draft.WithRdpFile)
        {
            lines.Add(LocalText.Of(nameof(Strings.LauncherCommentRdpFile), draft.RdpFileName).ToString());
        }

        lines.Add(Strings.LauncherCommentHelp);
        return lines;
    }

    /// <summary>The text for the user of a wrong field.</summary>
    public static string ErrorText(LauncherFieldError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var field = FieldLabel(error.Field).TrimEnd(':', ' ', (char)0xA0);
        if (error.Problem == LauncherProblem.TooLong)
        {
            return Strings.LauncherTooLong;
        }

        if (error.Problem == LauncherProblem.Exe)
        {
            return Strings.LauncherExeInvalid;
        }

        if (error.Problem == LauncherProblem.RdpFileName)
        {
            return Strings.LauncherNameInvalid;
        }

        return error.Rule is { } rule
            ? LocalText.Of(nameof(Strings.LauncherFieldInvalid), field, LaunchTexts.Rule(rule)).ToString()
            : LocalText.Of(nameof(Strings.LauncherFieldNeeded), field).ToString();
    }

    /// <summary>The label of a field.</summary>
    public static string FieldLabel(LauncherField field) => field switch
    {
        LauncherField.Name => Strings.LauncherName,
        LauncherField.Server => Strings.LauncherServer,
        LauncherField.Port => Strings.LauncherPort,
        LauncherField.User => Strings.LauncherUser,
        LauncherField.Domain => Strings.LauncherDomain,
        LauncherField.Program => Strings.LauncherProgram,
        LauncherField.DisplayName => Strings.LauncherDisplayName,
        LauncherField.Arguments => Strings.LauncherArguments,
        LauncherField.Gateway => Strings.LauncherGateway,
        _ => Strings.LauncherExe,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private static ComboBox NewCombo() => new() { DropDownStyle = ComboBoxStyle.DropDownList };

    private static Button NewButton() => new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, UseVisualStyleBackColor = true };

    private static T? Selected<T>(ComboBox combo) => combo.SelectedItem is Choice<T> choice ? choice.Value : default;

    private static void Select<T>(ComboBox combo, T value)
    {
        foreach (var item in combo.Items)
        {
            if (item is Choice<T> choice && EqualityComparer<T>.Default.Equals(choice.Value, value))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static void Fill<T>(ComboBox combo, params (T Value, string Text)[] choices)
    {
        combo.Items.Clear();
        foreach (var (value, text) in choices)
        {
            combo.Items.Add(new Choice<T>(value, text));
        }

        combo.SelectedIndex = 0;
    }

    private int Px(int logical) => (int)Math.Round(logical * _scale);

    private Padding Scaled(Padding padding) => new(Px(padding.Left), Px(padding.Top), Px(padding.Right), Px(padding.Bottom));

    private void BuildGrid()
    {
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        // The first view.
        Row(false, (Strings.LauncherName, _name, 3));
        Row(false, (Strings.LauncherServer, _server, 1), (Strings.LauncherPort, _port, 1));
        Row(false, (Strings.LauncherUser, _user, 3));
        Row(false, (null, Hint(Strings.LauncherPasswordNote), 3));
        Row(false, (Strings.LauncherKind, Flow(_kindDesktop, _kindRemoteApp), 3));
        Row(false, (Strings.LauncherProgram, _program, 3));
        Row(false, (null, Hint(Strings.LauncherProgramHint), 3));
        Row(false, (null, _showAdvanced, 3));

        // The advanced options.
        Row(true, (Strings.LauncherDisplayName, _displayName, 1), (Strings.LauncherArguments, _arguments, 1));
        Row(true, (Strings.LauncherDomain, _domain, 1), (Strings.LauncherGateway, _gateway, 1));
        Row(true, (Strings.LauncherWindow, _window, 1), (null, _askPassword, 1));
        Row(true, (Strings.LauncherMultimon, _multimon, 1), (Strings.LauncherScale, _scaleChoice, 1));
        Row(true, (Strings.LauncherWindowsKeys, _windowsKeys, 1), (Strings.LauncherAudio, _audio, 1));
        Row(true, (Strings.LauncherClipboard, _clipboard, 1), (Strings.LauncherDrives, _drives, 1));
        Row(true, (Strings.LauncherPrinters, _printers, 1));
        Row(true, (null, _trusted, 3));
        Row(true, (null, Hint(Strings.LauncherTrustedHint), 3));
        Row(true, (Strings.LauncherExe, _exeRunning, 3));
        Row(true, (null, _exeNext, 3));
        Row(true, (null, _exeTyped, 3));
        Row(true, (null, _typedExe, 3));
        Row(true, (null, Hint(Strings.LauncherExeTypedHint), 3));
        Row(true, (Strings.LauncherOutput, Flow(_outputBat, _outputBatRdp), 3));
    }

    /// <summary>
    /// A row of the grid: one or two fields with their labels; a field spans the columns given.
    /// A row of fields has the height of a text box, so that hidden rows of the advanced
    /// options take no room; a row of hints follows its text.
    /// </summary>
    private void Row(bool advanced, params (string? Label, Control Field, int Span)[] cells)
    {
        var row = _grid.RowCount++;
        var hints = cells.All(cell => cell.Label is null && cell.Field is Label);
        _grid.RowStyles.Add(hints ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, _rowHeight));
        if (advanced && !hints)
        {
            _advancedRows.Add(row);
        }

        var column = 0;
        foreach (var (text, field, span) in cells)
        {
            if (text is not null)
            {
                var label = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = Scaled(new Padding(0, 0, 8, 0)) };
                _grid.Controls.Add(label, column, row);
                if (advanced)
                {
                    _advanced.Add(label);
                }
            }

            field.Anchor = field is TextBox or ComboBox || field == _exeRunning ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left;
            field.Margin = hints
                ? Scaled(new Padding(0, 0, 0, 6))
                : Scaled(new Padding(0, 0, column == 0 && span == 1 ? 16 : 0, 0));
            if (field == _exeRunning)
            {
                field.Height = _rowHeight - Px(4);
            }

            _grid.Controls.Add(field, column + 1, row);
            _grid.SetColumnSpan(field, span);
            if (advanced)
            {
                _advanced.Add(field);
            }

            column += span + 1;
        }
    }

    private Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = HintColor, Tag = HintTag };

    /// <summary>
    /// Gives each hint the exact width of its cell, so that the table measures its lines as it
    /// shows them; a narrower measure would leave the extra height in the last row.
    /// </summary>
    private void FixHintWidths(int gridWidth)
    {
        var labelColumn = _grid.Controls.Cast<Control>()
            .Where(control => _grid.GetColumn(control) == 0)
            .Select(control => control.PreferredSize.Width + control.Margin.Horizontal)
            .DefaultIfEmpty(0)
            .Max();
        foreach (var hint in _grid.Controls.OfType<Label>().Where(label => ReferenceEquals(label.Tag, HintTag)))
        {
            var size = new Size(gridWidth - labelColumn - hint.Margin.Horizontal, 0);
            hint.MinimumSize = size;
            hint.MaximumSize = size;
        }
    }

    private FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
        foreach (var control in controls)
        {
            control.Margin = control is Button ? Scaled(new Padding(0, 0, 6, 0)) : Scaled(new Padding(0, 0, 16, 0));
            if (control is Button button)
            {
                button.MinimumSize = new Size(Px(88), 0);
            }

            flow.Controls.Add(control);
        }

        return flow;
    }

    private TableLayoutPanel BuildButtons(int width)
    {
        var buttons = new TableLayoutPanel { ColumnCount = 5, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, MinimumSize = new Size(width, 0), Margin = Scaled(new Padding(0, 6, 0, 0)) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 1; i < 5; i++)
        {
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        var column = 1;
        foreach (var button in new[] { _test, _copy, _save, _close })
        {
            button.MinimumSize = new Size(Px(88), 0);
            button.Margin = button == _close ? Padding.Empty : Scaled(new Padding(0, 0, 6, 0));
            buttons.Controls.Add(button, column++, 0);
        }

        return buttons;
    }

    private void ApplyTexts()
    {
        _kindDesktop.Text = Strings.LauncherKindDesktop;
        _kindRemoteApp.Text = Strings.LauncherKindRemoteApp;
        _showAdvanced.Text = Strings.LauncherAdvanced;
        _askPassword.Text = Strings.LauncherAskPassword;
        _trusted.Text = Strings.LauncherTrusted;
        _exeRunning.Text = LocalText.Of(nameof(Strings.LauncherExeRunning), _runningExe).ToString();
        _toolTip.SetToolTip(_exeRunning, _runningExe);
        _exeNext.Text = Strings.LauncherExeNext;
        _exeTyped.Text = Strings.LauncherExeTyped;
        _outputBat.Text = Strings.LauncherOutputBat;
        _outputBatRdp.Text = Strings.LauncherOutputBatRdp;
        _fillSession.Text = Strings.LauncherFillSession;
        _fillFile.Text = Strings.LauncherFillFile;
        _test.Text = Strings.LauncherTest;
        _toolTip.SetToolTip(_test, Strings.LauncherTestTip);
        _toolTip.SetToolTip(_window, Strings.LauncherWindowTip);
        _copy.Text = Strings.LauncherCopy;
        _save.Text = Strings.LauncherSave;
        _close.Text = Strings.LauncherClose;
        _typedExe.PlaceholderText = TypedExeSuggestion(_runningExe);
        _port.PlaceholderText = LaunchValues.DefaultPort.ToString(CultureInfo.InvariantCulture);
        _program.PlaceholderText = "||Notepad";

        var byDefault = Strings.LauncherChoiceDefault;
        Fill(_window, (LaunchWindow.Auto, Strings.LauncherWindowAuto), (LaunchWindow.Show, Strings.LauncherWindowShow), (LaunchWindow.Hidden, Strings.LauncherWindowHidden));
        Fill(_multimon, ((bool?)null, byDefault), (true, Strings.LauncherChoiceOn), (false, Strings.LauncherChoiceOff));
        Fill(_scaleChoice, new[] { ((int?)null, byDefault) }.Concat(new[] { 100, 125, 150, 175, 200, 250, 300, 400, 500 }.Select(value => ((int?)value, LocalText.Of(nameof(Strings.LauncherScaleValue), value).ToString()))).ToArray());
        Fill(_windowsKeys, ((WindowsKeysTarget?)null, byDefault), (WindowsKeysTarget.Local, Strings.LauncherKeysLocal), (WindowsKeysTarget.Remote, Strings.LauncherKeysRemote), (WindowsKeysTarget.FullScreen, Strings.LauncherKeysFullScreen));
        Fill(_audio, ((AudioTarget?)null, byDefault), (AudioTarget.Local, Strings.LauncherAudioLocal), (AudioTarget.Remote, Strings.LauncherAudioRemote), (AudioTarget.Off, Strings.LauncherAudioOff));
        Fill(_clipboard, ((bool?)null, byDefault), (true, Strings.LauncherChoiceOn), (false, Strings.LauncherChoiceOff));
        Fill(_drives, ((bool?)null, byDefault), (true, Strings.LauncherDrivesAll), (false, Strings.LauncherDrivesNone));
        Fill(_printers, ((bool?)null, byDefault), (true, Strings.LauncherChoiceOn), (false, Strings.LauncherChoiceOff));
    }

    /// <summary>The path to suggest for a typed path: the running one, with %LOCALAPPDATA% or %ProgramFiles% in place of its folder.</summary>
    private static string TypedExeSuggestion(string runningExe)
    {
        foreach (var (variable, folder) in new[] { ("LOCALAPPDATA", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), ("ProgramFiles", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) })
        {
            if (folder.Length > 0 && runningExe.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return $"%{variable}%{runningExe[folder.Length..]}";
            }
        }

        return runningExe;
    }

    private void WireEvents()
    {
        foreach (var box in new[] { _name, _server, _port, _user, _program, _domain, _gateway, _displayName, _arguments, _typedExe })
        {
            box.TextChanged += (_, _) => UpdatePreview();
        }

        foreach (var check in new ButtonBase[] { _kindDesktop, _kindRemoteApp, _askPassword, _trusted, _exeRunning, _exeNext, _exeTyped, _outputBat, _outputBatRdp })
        {
            switch (check)
            {
                case RadioButton radio:
                    radio.CheckedChanged += (_, _) => UpdatePreview();
                    break;
                case CheckBox box:
                    box.CheckedChanged += (_, _) => UpdatePreview();
                    break;
            }
        }

        foreach (var combo in new[] { _window, _multimon, _scaleChoice, _windowsKeys, _audio, _clipboard, _drives, _printers })
        {
            combo.SelectedIndexChanged += (_, _) => UpdatePreview();
        }

        _exeTyped.CheckedChanged += (_, _) =>
        {
            if (_exeTyped.Checked && _typedExe.Text.Length == 0)
            {
                _typedExe.Text = TypedExeSuggestion(_runningExe);
            }
        };
        _showAdvanced.CheckedChanged += (_, _) => ShowAdvanced(_showAdvanced.Checked);
        _fillSession.Click += (_, _) => FillFromSessionClicked?.Invoke(this, EventArgs.Empty);
        _fillFile.Click += (_, _) => FillFromFileClicked?.Invoke(this, EventArgs.Empty);
        _test.Click += (_, _) => TestClicked?.Invoke(this, EventArgs.Empty);
        _copy.Click += (_, _) => CopyClicked?.Invoke(this, EventArgs.Empty);
        _save.Click += (_, _) => SaveClicked?.Invoke(this, EventArgs.Empty);
        _close.Click += (_, _) => CloseClicked?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePreview()
    {
        if (_loading)
        {
            return;
        }

        var draft = Draft;
        _name.PlaceholderText = draft.DefaultName;
        var remoteApp = draft.RemoteApp;
        _program.Enabled = remoteApp;
        _displayName.Enabled = remoteApp;
        _arguments.Enabled = remoteApp;
        _typedExe.Enabled = draft.ExeKind == LauncherExeKind.WithVariables;
        _spec = draft.ToSpec(_runningExe, Comments(draft), out var error);
        _preview.Text = _spec is null ? string.Empty : LauncherBatch.Build(_spec);
        if (error is not null)
        {
            ShowStatus(ErrorText(error), error: true);
        }
        else
        {
            ShowStatus(draft.WithRdpFile ? LocalText.Of(nameof(Strings.LauncherWillWriteTwo), draft.FileBaseName + ".bat", draft.RdpFileName).ToString() : Strings.LauncherWillWriteOne, error: false);
        }

        _test.Enabled = _spec is not null;
        _copy.Enabled = _spec is not null;
        _save.Enabled = _spec is not null;
    }

    /// <summary>An entry of a list, with its text.</summary>
    private sealed record Choice<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }
}
