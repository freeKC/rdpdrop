using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using UltimateRDP.Client.Localization;
using UltimateRDP.Core.Launchers;

namespace UltimateRDP.Client.Launchers;

/// <summary>
/// Development aid, since 0.10.0: <c>UltimateRDP.exe --render-launcher &lt;folder&gt;</c> writes PNG
/// previews of the launcher generator, in French, short and with the advanced options, at
/// 100 % and 200 % (or the scales given after the folder, such as 100), and two launchers it
/// writes as text files, without showing any window. The
/// panel of the dialog is created in a window that is never shown and drawn into a bitmap; a
/// plain title bar and border stand for the frame that Windows draws. The fields hold
/// placeholders only.
/// </summary>
internal static class LauncherPreview
{
    /// <summary>The switch of the command line. Not shown by --help: it is not for users.</summary>
    public const string Switch = "--render-launcher";

    /// <summary>A path of UltimateRDP.exe for the previews, in place of the real one.</summary>
    private const string SampleExe = @"C:\Users\utilisateur\AppData\Local\UltimateRDP\client-0.10.0\UltimateRDP.exe";

    private const string SampleTypedExe = @"%LOCALAPPDATA%\UltimateRDP\client-0.10.0\UltimateRDP.exe";

    private const int GwlStyle = -16;

    private const long WsVisible = 0x10000000;

    /// <summary>What the previews could not draw as the dialog does, if anything.</summary>
    private static string _notes = string.Empty;

    /// <summary>Writes the previews at <paramref name="scales"/> (100, 200 or both when empty) into <paramref name="folder"/>.</summary>
    public static int Render(string folder, IReadOnlyList<string> scales)
    {
        Directory.CreateDirectory(folder);
        UiLanguage.Apply("fr", log: false);
        var written = new List<string>();
        var shortDraft = new LauncherDraft { Server = "server.example.com", User = "utilisateur", RemoteApp = true, Program = "||BlocNotes", DisplayName = "Bloc-notes" };
        var advancedDraft = shortDraft with
        {
            Domain = "EXEMPLE",
            Gateway = "passerelle.example.com",
            Arguments = "/A \"C:\\Données\\notes 2026.txt\"",
            AskPassword = true,
            Scale = 125,
            Clipboard = true,
            Drives = true,
            Audio = AudioTarget.Local,
            ExeKind = LauncherExeKind.WithVariables,
            TypedExe = SampleTypedExe,
        };
        var chosen = scales.Count == 0 ? [1f, 2f] : scales.Select(text => float.Parse(text, CultureInfo.InvariantCulture) / 100f).ToArray();
        foreach (var scale in chosen)
        {
            foreach (var (name, draft, advanced) in new[] { ("short", shortDraft, false), ("advanced", advancedDraft, true) })
            {
                var file = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"launcher-{name}-light-{scale * 100:0}.png"));
                using (var bitmap = Draw(scale, draft, advanced))
                {
                    bitmap.Save(file, ImageFormat.Png);
                }

                written.Add(Path.GetFileName(file));
            }
        }

        var desktop = new LauncherDraft { Server = "server.example.com", User = "utilisateur", ExeKind = LauncherExeKind.WithVariables, TypedExe = SampleTypedExe };
        var remoteApp = advancedDraft with { Name = "Bloc-notes" };
        foreach (var (name, draft) in new[] { ("desktop", desktop), ("remoteapp", remoteApp) })
        {
            var spec = draft.ToSpec(SampleExe, LauncherPanel.Comments(draft), out var error) ?? throw new InvalidOperationException($"The sample {name} is refused: {error}");
            var file = Path.Combine(folder, $"launcher-{name}.bat.txt");
            File.WriteAllBytes(file, LauncherBatch.FileEncoding.GetBytes(LauncherBatch.Build(spec)));
            written.Add(Path.GetFileName(file));
        }

        File.WriteAllText(Path.Combine(folder, "files.txt"), string.Join("\r\n", written) + "\r\n" + _notes + "\r\n");
        return 0;
    }

    /// <summary>
    /// The dialog at <paramref name="scale"/>. Windows draws the boxes of the check boxes and
    /// radio buttons at the DPI of the window, which is the DPI of the process: the 100 %
    /// previews are made by a process that Windows starts without DPI awareness
    /// (<c>__COMPAT_LAYER=DpiUnaware</c>), the 200 % ones by a process on a monitor at 200 %.
    /// files.txt says when the DPI did not match.
    /// </summary>
    private static Bitmap Draw(float scale, LauncherDraft draft, bool advanced) => DrawAtCurrentDpi(scale, draft, advanced);

    private static Bitmap DrawAtCurrentDpi(float scale, LauncherDraft draft, bool advanced)
    {
        using var host = new Form
        {
            AutoScaleMode = AutoScaleMode.None,
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        using var panel = LauncherForm.NewPanel(scale, SampleExe, canFillFromSession: true, out var font, out var monoFont);
        using (font)
        using (monoFont)
        {
            host.Font = font;
            host.Controls.Add(panel);
            panel.SetDraft(draft);
            panel.ShowAdvanced(advanced);

            // The handles of every control, parents first, without showing the window: the
            // drawing goes through the windows that exist.
            CreateHandles(host);
            host.PerformLayout();
            if (host.DeviceDpi != (int)(96 * scale))
            {
                _notes += string.Create(CultureInfo.InvariantCulture, $"The {scale * 100:0} % previews were drawn in windows of {host.DeviceDpi} DPI: the boxes of the check boxes and radio buttons have the size of that DPI. ");
            }

            using var content = new Bitmap(panel.Width, panel.Height, PixelFormat.Format24bppRgb);
            panel.DrawToBitmap(content, new Rectangle(Point.Empty, panel.Size));
            using (var graphics = Graphics.FromImage(content))
            {
                DrawComboTexts(graphics, panel, panel);
            }

            var title = (int)Math.Round(32 * scale);
            var border = Math.Max(1, (int)Math.Round(scale));
            var canvas = new Bitmap(content.Width + (2 * border), content.Height + title + border, PixelFormat.Format24bppRgb);
            using var canvasGraphics = Graphics.FromImage(canvas);
            canvasGraphics.Clear(Color.FromArgb(160, 160, 160));
            using (var bar = new SolidBrush(Color.White))
            {
                canvasGraphics.FillRectangle(bar, border, border, content.Width, title - border);
            }

            TextRenderer.DrawText(canvasGraphics, Resources.Strings.LauncherTitle, font, new Rectangle(border + (int)(12 * scale), border, content.Width, title - border), Color.Black, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            canvasGraphics.DrawImage(content, border, title);
            return canvas;
        }
    }

    /// <summary>
    /// A drop-down list does not draw its text into a bitmap: the text of each one shown is
    /// drawn where the list shows it, left of its arrow.
    /// </summary>
    private static void DrawComboTexts(Graphics graphics, Control root, Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (!HasVisibleStyle(child))
            {
                continue;
            }

            if (child is ComboBox combo)
            {
                var scale = combo.DeviceDpi / 96f;
                var bounds = new Rectangle(root.PointToClient(combo.PointToScreen(Point.Empty)), combo.Size);
                var arrow = (int)(SystemInformation.VerticalScrollBarWidth * scale);
                var text = new Rectangle(bounds.X + (int)(4 * scale), bounds.Y, bounds.Width - arrow - (int)(6 * scale), bounds.Height);
                using (var back = new SolidBrush(SystemColors.Window))
                {
                    graphics.FillRectangle(back, text);
                }

                // GDI+ text: GDI text drawn on a bitmap comes out bold, as for the previews of the loader.
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var format = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
                graphics.DrawString(combo.Text, combo.Font, SystemBrushes.ControlText, text, format);
            }

            DrawComboTexts(graphics, root, child);
        }
    }

    /// <summary>WS_VISIBLE of the window of <paramref name="control"/>: the window is hidden, so <see cref="Control.Visible"/> cannot tell.</summary>
    private static bool HasVisibleStyle(Control control) => (GetWindowLongPtr(control.Handle, GwlStyle).ToInt64() & WsVisible) != 0;

    private static void CreateHandles(Control control)
    {
        _ = control.Handle;
        foreach (Control child in control.Controls)
        {
            CreateHandles(child);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
