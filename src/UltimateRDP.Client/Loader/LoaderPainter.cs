using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace UltimateRDP.Client.Loader;

/// <summary>The colors of the loader, light or dark after the app theme of Windows.</summary>
internal sealed record LoaderTheme(Color Back, Color Border, Color Text, Color Secondary, Color Accent, Color Track, bool IsDark)
{
    public static LoaderTheme Light { get; } = new(Color.FromArgb(249, 249, 249), Color.FromArgb(214, 214, 214), Color.FromArgb(26, 26, 26), Color.FromArgb(96, 96, 96), Color.FromArgb(0, 103, 192), Color.FromArgb(224, 224, 224), false);

    public static LoaderTheme Dark { get; } = new(Color.FromArgb(43, 43, 43), Color.FromArgb(64, 64, 64), Color.FromArgb(242, 242, 242), Color.FromArgb(184, 184, 184), Color.FromArgb(76, 194, 255), Color.FromArgb(72, 72, 72), true);

    /// <summary>Dark when Windows says that the apps use the dark theme (AppsUseLightTheme 0), light otherwise.</summary>
    public static LoaderTheme FromWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0 ? Dark : Light;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Light;
        }
    }
}

/// <summary>What the loader shows, in the language of the user interface.</summary>
/// <param name="Title">What is opening, the program or the server; null before the .rdp file is read.</param>
/// <param name="Step">The line of the step.</param>
/// <param name="Slow">The second line, once the server takes longer than usual; null before.</param>
/// <param name="Cancel">The text of Cancel, or null when there is nothing to cancel yet.</param>
/// <param name="CancelHot">True while the mouse is over Cancel.</param>
/// <param name="Phase">Where the moving bar is, from 0 to 1.</param>
internal sealed record LoaderVisual(string? Title, string Step, string? Slow, string? Cancel, bool CancelHot, double Phase);

/// <summary>The fonts of the loader at one scale.</summary>
internal sealed class LoaderFonts : IDisposable
{
    public LoaderFonts(float scale)
    {
        Scale = scale;
        Brand = new Font("Segoe UI", Pixels(9f), FontStyle.Regular, GraphicsUnit.Pixel);
        Title = new Font("Segoe UI Semibold", Pixels(13f), FontStyle.Regular, GraphicsUnit.Pixel);
        Step = new Font("Segoe UI", Pixels(9.5f), FontStyle.Regular, GraphicsUnit.Pixel);
        Link = new Font("Segoe UI", Pixels(9f), FontStyle.Regular, GraphicsUnit.Pixel);
        LinkHot = new Font("Segoe UI", Pixels(9f), FontStyle.Underline, GraphicsUnit.Pixel);
        Small = new Font("Segoe UI", Pixels(8.5f), FontStyle.Regular, GraphicsUnit.Pixel);
    }

    public float Scale { get; }

    public Font Brand { get; }

    public Font Title { get; }

    public Font Step { get; }

    public Font Link { get; }

    public Font LinkHot { get; }

    public Font Small { get; }

    public void Dispose()
    {
        Brand.Dispose();
        Title.Dispose();
        Step.Dispose();
        Link.Dispose();
        LinkHot.Dispose();
        Small.Dispose();
    }

    /// <summary>Points at 96 DPI to pixels at the scale.</summary>
    private float Pixels(float points) => points * 96f / 72f * Scale;
}

/// <summary>
/// Draws the loader, since 0.9.3: the same code paints the window and the previews of the
/// development switch <c>--render-loader</c>, so that what the previews show is what the window
/// shows. Everything is drawn here, the mark of the application and the moving bar included: no
/// image file and no animation file ship with the client.
/// </summary>
internal static class LoaderPainter
{
    /// <summary>The size of the loader at 96 DPI.</summary>
    public static readonly Size LogicalSize = new(380, 136);

    private const int Padding = 20;
    private const int IconSize = 36;
    private const int BarTop = 80;
    private const int BarHeight = 4;
    private const int StepTop = 92;
    private const int SlowTop = 112;

    private static readonly Dictionary<(string Text, float Size, FontStyle Style), float> Measures = [];

    /// <summary>The share of the track that the moving bar covers.</summary>
    private const double BarShare = 0.3;

    /// <summary>The time the bar takes to cross the track once.</summary>
    public const int PeriodMilliseconds = 1600;

    /// <summary>Paints the whole loader into <paramref name="size"/> pixels.</summary>
    public static void Paint(Graphics graphics, Size size, LoaderTheme theme, LoaderVisual visual, LoaderFonts fonts)
    {
        var s = fonts.Scale;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var back = new SolidBrush(theme.Back))
        {
            graphics.FillRectangle(back, 0, 0, size.Width, size.Height);
        }

        using (var border = new Pen(theme.Border, Math.Max(1f, s)))
        {
            var inset = border.Width / 2f;
            graphics.DrawRectangle(border, inset, inset, size.Width - border.Width, size.Height - border.Width);
        }

        DrawMark(graphics, new RectangleF(Padding * s, (Padding - 2) * s, IconSize * s, IconSize * s));
        var textLeft = (int)((Padding + IconSize + 12) * s);
        var textWidth = size.Width - textLeft - (int)(Padding * s);
        if (visual.Title is null)
        {
            Text(graphics, AppInfo.Name, fonts.Title, new Rectangle(textLeft, (int)(Padding * s), textWidth, (int)(IconSize * s) - (int)(4 * s)), theme.Text);
        }
        else
        {
            Text(graphics, AppInfo.Name, fonts.Brand, new Rectangle(textLeft, (int)((Padding - 4) * s), textWidth, (int)(16 * s)), theme.Secondary);
            Text(graphics, visual.Title, fonts.Title, new Rectangle(textLeft, (int)((Padding + 10) * s), textWidth, (int)(24 * s)), theme.Text);
        }

        DrawBar(graphics, new RectangleF(Padding * s, BarTop * s, size.Width - (2 * Padding * s), BarHeight * s), theme, visual.Phase);

        var cancel = visual.Cancel is null ? Rectangle.Empty : CancelBounds(size, fonts, visual.Cancel);
        var stepRight = cancel.IsEmpty ? size.Width - (int)(Padding * s) : cancel.Left - (int)(12 * s);
        Text(graphics, visual.Step, fonts.Step, new Rectangle((int)(Padding * s), (int)(StepTop * s), stepRight - (int)(Padding * s), (int)(18 * s)), theme.Text);
        if (visual.Cancel is { } cancelText)
        {
            Text(graphics, cancelText, visual.CancelHot ? fonts.LinkHot : fonts.Link, cancel, theme.Accent);
        }

        if (visual.Slow is { } slow)
        {
            Text(graphics, slow, fonts.Small, new Rectangle((int)(Padding * s), (int)(SlowTop * s), size.Width - (int)(2 * Padding * s), (int)(16 * s)), theme.Secondary);
        }
    }

    /// <summary>Where Cancel is, for the paint and for the clicks.</summary>
    public static Rectangle CancelBounds(Size size, LoaderFonts fonts, string cancel)
    {
        var s = fonts.Scale;
        var width = (int)Math.Ceiling(MeasureWidth(cancel, fonts.LinkHot)) + (int)(2 * s);
        return new Rectangle(size.Width - (int)(Padding * s) - width, (int)(StepTop * s), width, (int)(18 * s));
    }

    /// <summary>The area of the moving bar, to repaint it alone at each frame.</summary>
    public static Rectangle BarBounds(Size size, float scale) =>
        Rectangle.Inflate(new Rectangle((int)(Padding * scale), (int)(BarTop * scale), size.Width - (int)(2 * Padding * scale), (int)(BarHeight * scale) + 1), 2, 2);

    /// <summary>The phase of the bar at <paramref name="milliseconds"/> since the start of the animation.</summary>
    public static double PhaseAt(long milliseconds) => (milliseconds % PeriodMilliseconds) / (double)PeriodMilliseconds;

    /// <summary>The mark of UltimateRDP, as the agent draws its tray icon: an arrow into a tray, white on blue.</summary>
    public static void DrawMark(Graphics graphics, RectangleF bounds)
    {
        var s = bounds.Width;
        using (var background = RoundedRectangle(bounds, s / 5f))
        using (var fill = new SolidBrush(Color.FromArgb(0, 103, 192)))
        {
            graphics.FillPath(fill, background);
        }

        using var pen = new Pen(Color.White, Math.Max(1.5f, s / 11f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        PointF P(float x, float y) => new(bounds.X + (s * x), bounds.Y + (s * y));
        graphics.DrawLine(pen, P(0.5f, 0.2f), P(0.5f, 0.58f));
        graphics.DrawLines(pen, [P(0.33f, 0.42f), P(0.5f, 0.59f), P(0.67f, 0.42f)]);
        graphics.DrawLines(pen, [P(0.23f, 0.62f), P(0.23f, 0.77f), P(0.77f, 0.77f), P(0.77f, 0.62f)]);
    }

    /// <summary>A rounded rectangle.</summary>
    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// The track, and a segment of <see cref="BarShare"/> of it that crosses it from left to right
    /// once per period, faster in the middle than at the ends.
    /// </summary>
    private static void DrawBar(Graphics graphics, RectangleF track, LoaderTheme theme, double phase)
    {
        var radius = track.Height / 2f;
        using (var trackPath = RoundedRectangle(track, radius))
        using (var trackBrush = new SolidBrush(theme.Track))
        {
            graphics.FillPath(trackBrush, trackPath);
        }

        var eased = phase * phase * (3 - (2 * phase));
        var width = (float)(track.Width * BarShare);
        var left = track.X - width + (float)((track.Width + width) * eased);
        var segment = RectangleF.Intersect(new RectangleF(left, track.Y, width, track.Height), track);
        if (segment.Width <= 0)
        {
            return;
        }

        using var path = RoundedRectangle(segment, radius);
        using var brush = new SolidBrush(theme.Accent);
        graphics.FillPath(brush, path);
    }

    /// <summary>The width that <see cref="Text"/> takes for <paramref name="text"/>, measured the same way.</summary>
    private static float MeasureWidth(string text, Font font)
    {
        lock (Measures)
        {
            var key = (text, font.Size, font.Style);
            if (!Measures.TryGetValue(key, out var width))
            {
                using var bitmap = new Bitmap(1, 1);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                width = graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericDefault).Width;
                Measures[key] = width;
            }

            return width;
        }
    }

    /// <summary>
    /// Text with the antialiasing of GDI+, the same on the screen and in a bitmap: GDI text in a
    /// bitmap, as for the previews, loses the edges of ClearType and looks bold at 100 %.
    /// </summary>
    private static void Text(Graphics graphics, string text, Font font, Rectangle bounds, Color color)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            HotkeyPrefix = System.Drawing.Text.HotkeyPrefix.None,
        };
        var hint = graphics.TextRenderingHint;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        graphics.DrawString(text, font, brush, bounds, format);
        graphics.TextRenderingHint = hint;
    }
}
