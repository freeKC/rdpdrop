using System.Drawing.Drawing2D;
using UltimateRDP.Agent.Interop;

namespace UltimateRDP.Agent.Tray;

/// <summary>
/// Draws the tray icon at run time: a blue rounded square with a white arrow pointing
/// down into a tray, so no image file has to ship with the agent.
/// </summary>
internal sealed class TrayIconFactory : IDisposable
{
    private IntPtr _handle;

    public TrayIconFactory(Size size)
    {
        using var bitmap = Draw(Math.Max(16, size.Width));
        _handle = bitmap.GetHicon();
        Icon = Icon.FromHandle(_handle);
    }

    /// <summary>The icon. Valid until the factory is disposed.</summary>
    public Icon Icon { get; }

    public void Dispose()
    {
        Icon.Dispose();
        if (_handle != IntPtr.Zero)
        {
            User32.DestroyIcon(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static Bitmap Draw(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        float s = size;
        var inset = s / 16f;
        var square = new RectangleF(inset, inset, s - 2 * inset, s - 2 * inset);
        using (var background = RoundedRectangle(square, s / 5f))
        using (var fill = new SolidBrush(Color.FromArgb(0, 103, 192)))
        {
            graphics.FillPath(fill, background);
        }

        using var pen = new Pen(Color.White, Math.Max(1.5f, s / 9f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        // Arrow.
        var center = s / 2f;
        graphics.DrawLine(pen, center, s * 0.22f, center, s * 0.58f);
        graphics.DrawLines(pen, new[]
        {
            new PointF(s * 0.34f, s * 0.43f),
            new PointF(center, s * 0.59f),
            new PointF(s * 0.66f, s * 0.43f),
        });

        // Tray.
        graphics.DrawLines(pen, new[]
        {
            new PointF(s * 0.24f, s * 0.62f),
            new PointF(s * 0.24f, s * 0.76f),
            new PointF(s * 0.76f, s * 0.76f),
            new PointF(s * 0.76f, s * 0.62f),
        });

        return bitmap;
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
