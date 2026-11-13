using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace UltimateRDP.Client.Previews;

/// <summary>
/// Development aid, since 0.10.0: draws controls into bitmaps in windows that are never shown,
/// for the previews and the images of the README. Every handle is created, parents first, and
/// each control prints itself (<see cref="Control.DrawToBitmap"/>). What Windows does not print
/// into a bitmap is drawn over it: the text of a drop-down list. A plain title bar and border
/// stand for the frame that Windows draws around a window.
/// </summary>
internal static class HiddenRender
{
    private const int GwlStyle = -16;
    private const long WsVisible = 0x10000000;

    /// <summary>Creates the handle of <paramref name="control"/> and of everything in it, parents first, without showing anything.</summary>
    public static void CreateHandles(Control control)
    {
        _ = control.Handle;
        foreach (Control child in control.Controls)
        {
            CreateHandles(child);
        }
    }

    /// <summary>Lays out <paramref name="control"/> and everything in it, parents first.</summary>
    private static void LayOut(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls)
        {
            LayOut(child);
        }
    }

    /// <summary>
    /// The client area of <paramref name="parent"/>: its background, then each child that is
    /// not hidden, with what it holds, then the texts of its drop-down lists.
    /// </summary>
    public static Bitmap DrawClient(Control parent)
    {
        CreateHandles(parent);
        LayOut(parent);
        var bitmap = new Bitmap(Math.Max(1, parent.ClientSize.Width), Math.Max(1, parent.ClientSize.Height), PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(parent.BackColor);
        }

        // The last control of the collection is at the bottom of the z order: drawn first.
        foreach (var child in parent.Controls.Cast<Control>().Reverse())
        {
            if (HasVisibleStyle(child) && child.Width > 0 && child.Height > 0)
            {
                child.DrawToBitmap(bitmap, child.Bounds);
            }
        }

        using (var graphics = Graphics.FromImage(bitmap))
        {
            DrawWhatIsNotPrinted(graphics, parent, parent);
        }

        return bitmap;
    }

    /// <summary>The bounds of <paramref name="control"/> in the client area of <paramref name="root"/>.</summary>
    private static Rectangle BoundsIn(Control root, Control control) => new(root.PointToClient(control.PointToScreen(Point.Empty)), control.Size);

    /// <summary>The whole of <paramref name="control"/>, as it prints itself, with the texts of its drop-down lists.</summary>
    public static Bitmap DrawControl(Control control)
    {
        CreateHandles(control);
        control.PerformLayout();
        var bitmap = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height), PixelFormat.Format24bppRgb);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
        using (var graphics = Graphics.FromImage(bitmap))
        {
            DrawWhatIsNotPrinted(graphics, control, control);
        }

        return bitmap;
    }

    /// <summary>
    /// A drop-down menu or a context menu, laid out at its own size as it would open, without
    /// showing it.
    /// </summary>
    public static Bitmap DrawDropDown(ToolStripDropDown dropDown)
    {
        _ = dropDown.Handle;
        dropDown.PerformLayout();
        dropDown.Size = dropDown.GetPreferredSize(Size.Empty);
        dropDown.PerformLayout();
        var bitmap = new Bitmap(dropDown.Width, dropDown.Height, PixelFormat.Format24bppRgb);
        dropDown.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dropDown.Size));
        return bitmap;
    }

    /// <summary><paramref name="content"/> in a plain frame: a white title bar with <paramref name="title"/>, a gray border.</summary>
    public static Bitmap Frame(Bitmap content, string title, Font font, float scale)
    {
        var titleHeight = (int)Math.Round(32 * scale);
        var border = Math.Max(1, (int)Math.Round(scale));
        var canvas = new Bitmap(content.Width + (2 * border), content.Height + titleHeight + border, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(canvas);
        graphics.Clear(Color.FromArgb(160, 160, 160));
        using (var bar = new SolidBrush(Color.White))
        {
            graphics.FillRectangle(bar, border, border, content.Width, titleHeight - border);
        }

        // GDI+ text: GDI text drawn on a bitmap comes out bold.
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var format = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        graphics.DrawString(title, font, Brushes.Black, new RectangleF(border + (12 * scale), border, content.Width - (24 * scale), titleHeight - border), format);
        graphics.DrawImage(content, border, titleHeight);
        return canvas;
    }

    /// <summary>Puts <paramref name="overlay"/> over <paramref name="image"/> at <paramref name="at"/>, with a thin shadow, as an open menu.</summary>
    public static void Overlay(Bitmap image, Bitmap overlay, Point at)
    {
        using var graphics = Graphics.FromImage(image);
        using (var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
        {
            graphics.FillRectangle(shadow, at.X + 3, at.Y + 3, overlay.Width, overlay.Height);
        }

        graphics.DrawImage(overlay, at);
    }

    /// <summary>A plain background around <paramref name="image"/>, as a window on a desktop.</summary>
    public static Bitmap OnBackground(Bitmap image, int margin, Color back)
    {
        var canvas = new Bitmap(image.Width + (2 * margin), image.Height + (2 * margin), PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(canvas);
        graphics.Clear(back);
        graphics.DrawImage(image, margin, margin);
        return canvas;
    }

    /// <summary>WS_VISIBLE of the window of <paramref name="control"/>: its window is hidden, so <see cref="Control.Visible"/> cannot tell.</summary>
    public static bool HasVisibleStyle(Control control) => (GetWindowLongPtr(control.Handle, GwlStyle).ToInt64() & WsVisible) != 0;

    /// <summary>
    /// A drop-down list does not print its text into a bitmap: the text of each one shown is
    /// drawn where the list shows it, left of its arrow, with GDI+ (GDI text drawn on a bitmap
    /// comes out bold).
    /// </summary>
    private static void DrawWhatIsNotPrinted(Graphics graphics, Control root, Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (!HasVisibleStyle(child))
            {
                continue;
            }

            if (child is ProgressBar bar)
            {
                // A progress bar prints its frame but not its value.
                var bounds = BoundsIn(root, bar);
                ProgressBarRenderer.DrawHorizontalBar(graphics, bounds);
                var share = bar.Maximum > bar.Minimum ? (bar.Value - bar.Minimum) / (double)(bar.Maximum - bar.Minimum) : 0;
                var inner = Rectangle.Inflate(bounds, -2, -2);
                if (bar.Style != ProgressBarStyle.Marquee && share > 0)
                {
                    ProgressBarRenderer.DrawHorizontalChunks(graphics, inner with { Width = (int)(inner.Width * share) });
                }
            }

            if (child is TabControl tabs && tabs.TabCount > 0)
            {
                // The tabs of a tab control are not printed by a hidden window.
                var origin = BoundsIn(root, tabs).Location;
                for (var i = 0; i < tabs.TabCount; i++)
                {
                    var tab = tabs.GetTabRect(i);
                    tab.Offset(origin);
                    System.Windows.Forms.VisualStyles.TabItemState state = i == tabs.SelectedIndex ? System.Windows.Forms.VisualStyles.TabItemState.Selected : System.Windows.Forms.VisualStyles.TabItemState.Normal;
                    TabRenderer.DrawTabItem(graphics, tab, tabs.TabPages[i].Text, tabs.Font, false, state);
                }
            }

            if (child is ComboBox combo)
            {
                var scale = combo.DeviceDpi / 96f;
                var bounds = BoundsIn(root, combo);
                var arrow = (int)(SystemInformation.VerticalScrollBarWidth * scale);
                var text = new Rectangle(bounds.X + (int)(4 * scale), bounds.Y, bounds.Width - arrow - (int)(6 * scale), bounds.Height);
                using (var back = new SolidBrush(SystemColors.Window))
                {
                    graphics.FillRectangle(back, text);
                }

                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var format = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
                graphics.DrawString(combo.Text, combo.Font, SystemBrushes.ControlText, text, format);
            }

            DrawWhatIsNotPrinted(graphics, root, child);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
