using System.Diagnostics;
using System.Text;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Launchers;
using UltimateRDP.Core.RdpFiles;

namespace UltimateRDP.Client.Launchers;

/// <summary>
/// File, Create a launcher (.bat), since 0.10.0: a dialog that writes a .bat file that opens one
/// connection with UltimateRDP, for the end users of an administrator. It holds no password.
/// </summary>
/// <remarks>
/// The dialog takes the size of its <see cref="LauncherPanel"/>, which it creates for the DPI
/// of its monitor, and again, with the same fields, when it moves to a monitor with another
/// DPI.
/// </remarks>
internal sealed class LauncherForm : Form
{
    private readonly string _runningExe;
    private readonly RdpFile? _sessionFile;
    private LauncherPanel _panel;
    private int _panelDpi;
    private Font? _font;
    private Font? _monoFont;

    /// <param name="runningExe">The path of the running UltimateRDP.exe.</param>
    /// <param name="sessionFile">The .rdp settings of the active tab, or null without a tab.</param>
    public LauncherForm(string runningExe, RdpFile? sessionFile)
    {
        _runningExe = runningExe;
        _sessionFile = sessionFile;
        Text = Strings.LauncherTitle;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        _panelDpi = DeviceDpi;
        _panel = CreatePanel(_panelDpi / 96f, sessionFile is null ? new LauncherDraft() : LauncherDraft.FromRdpFile(sessionFile, new LauncherDraft()), advanced: false);
    }

    /// <summary>The fonts and the panel at <paramref name="scale"/>, for the dialog and the previews.</summary>
    public static LauncherPanel NewPanel(float scale, string runningExe, bool canFillFromSession, out Font font, out Font monoFont)
    {
        // In pixels, so that the size does not depend on the DPI Windows gives the process.
        font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 12f * scale, GraphicsUnit.Pixel);
        monoFont = new Font("Consolas", 12f * scale, GraphicsUnit.Pixel);
        return new LauncherPanel(scale, font, monoFont, runningExe, canFillFromSession);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // The window can open on a monitor of another DPI than the one known before it existed.
        Rescale(DeviceDpi);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Rescale(e.DeviceDpiNew);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ClientLog.Info($"Launcher generator opened at {DeviceDpi} DPI, {(_sessionFile is null ? "empty" : "filled from the active tab")}.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font?.Dispose();
            _monoFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Rescale(int dpi)
    {
        if (dpi == _panelDpi)
        {
            return;
        }

        ClientLog.Info($"Launcher generator on a monitor of {dpi} DPI instead of {_panelDpi}; its content is created again.");
        _panelDpi = dpi;
        var draft = _panel.Draft;
        var advanced = _panel.AdvancedShown;
        var old = _panel;
        SuspendLayout();
        Controls.Remove(old);
        _panel = CreatePanel(dpi / 96f, draft, advanced);
        ResumeLayout(true);
        old.Dispose();
    }

    private LauncherPanel CreatePanel(float scale, LauncherDraft draft, bool advanced)
    {
        var oldFont = _font;
        var oldMono = _monoFont;
        var panel = NewPanel(scale, _runningExe, _sessionFile is not null, out var font, out var monoFont);
        _font = font;
        _monoFont = monoFont;
        Font = font;
        panel.SetDraft(draft);
        panel.ShowAdvanced(advanced);
        panel.FillFromSessionClicked += (_, _) => FillFrom(_sessionFile);
        panel.FillFromFileClicked += (_, _) => FillFromFile();
        panel.TestClicked += (_, _) => Test();
        panel.CopyClicked += (_, _) => Copy();
        panel.SaveClicked += (_, _) => Save();
        panel.CloseClicked += (_, _) => Close();
        CancelButton = panel.CloseButton;
        Controls.Add(panel);
        oldFont?.Dispose();
        oldMono?.Dispose();
        return panel;
    }

    private void FillFrom(RdpFile? file)
    {
        if (file is not null)
        {
            _panel.SetDraft(LauncherDraft.FromRdpFile(file, _panel.Draft));
        }
    }

    private void FillFromFile()
    {
        if (MainForm.AskForRdpFile(this) is not { } path)
        {
            return;
        }

        try
        {
            FillFrom(RdpFile.Load(path));
            ClientLog.Info($"Launcher generator filled from {path}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ClientLog.Error($"Cannot read {path}", ex);
            _panel.ShowStatus(LocalText.Of(nameof(Strings.CannotReadFile), path, ex.Message).ToString(), error: true);
        }
    }

    /// <summary>Starts this UltimateRDP.exe with the options the launcher gives, all on the command line.</summary>
    private void Test()
    {
        if (_panel.Spec is not { } spec)
        {
            return;
        }

        var start = new ProcessStartInfo(_runningExe) { UseShellExecute = false };
        foreach (var arg in LauncherBatch.Arguments(spec with { RdpFileName = null }))
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start);
            ClientLog.Info($"Launcher test: {_runningExe} started with {start.ArgumentList.Count} argument(s).");
            _panel.ShowStatus(Strings.LauncherTestStarted, error: false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ClientLog.Error("The launcher test cannot start the client", ex);
            _panel.ShowStatus(LocalText.Of(nameof(Strings.LauncherCannotStart), ex.Message).ToString(), error: true);
        }
    }

    private void Copy()
    {
        if (_panel.Spec is not { } spec)
        {
            return;
        }

        try
        {
            Clipboard.SetText(LauncherBatch.Build(spec));
            _panel.ShowStatus(Strings.LauncherCopied, error: false);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            ClientLog.Error("Cannot put the launcher in the clipboard", ex);
            _panel.ShowStatus(LocalText.Of(nameof(Strings.LauncherCannotCopy), ex.Message).ToString(), error: true);
        }
    }

    /// <summary>
    /// Writes the .bat file where the user says, UTF-8 without a byte order mark; with a .rdp
    /// file, that file goes next to it, in UTF-16 like the files of mstsc, named after the
    /// .bat file chosen.
    /// </summary>
    private void Save()
    {
        if (_panel.Spec is null)
        {
            return;
        }

        var draft = _panel.Draft;
        using var dialog = new SaveFileDialog
        {
            Title = Strings.LauncherSaveTitle,
            Filter = Strings.LauncherSaveFilter,
            DefaultExt = "bat",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = draft.FileBaseName + ".bat",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var batPath = dialog.FileName;
        var named = draft with { Name = Path.GetFileNameWithoutExtension(batPath) };
        if (named.ToSpec(_runningExe, LauncherPanel.Comments(named), out var error) is not { } spec)
        {
            _panel.ShowStatus(error is null ? Strings.LauncherNameInvalid : LauncherPanel.ErrorText(error), error: true);
            return;
        }

        var rdpPath = spec.RdpFileName is { } rdpName ? Path.Combine(Path.GetDirectoryName(batPath) ?? string.Empty, rdpName) : null;
        if (rdpPath is not null && File.Exists(rdpPath)
            && MessageBox.Show(this, LocalText.Of(nameof(Strings.LauncherReplaceRdp), rdpPath).ToString(), AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            if (rdpPath is not null)
            {
                File.WriteAllText(rdpPath, LauncherBatch.RdpContent(spec.Options.Connection), Encoding.Unicode);
            }

            File.WriteAllText(batPath, LauncherBatch.Build(spec), LauncherBatch.FileEncoding);
            ClientLog.Info($"Launcher written: {batPath}{(rdpPath is null ? string.Empty : " and " + rdpPath)}.");
            _panel.ShowStatus(
                rdpPath is null
                    ? LocalText.Of(nameof(Strings.LauncherSavedOne), batPath).ToString()
                    : LocalText.Of(nameof(Strings.LauncherSavedTwo), batPath, rdpPath).ToString(),
                error: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ClientLog.Error($"Cannot write the launcher {batPath}", ex);
            _panel.ShowStatus(LocalText.Of(nameof(Strings.LauncherCannotSave), batPath, ex.Message).ToString(), error: true);
        }
    }
}
