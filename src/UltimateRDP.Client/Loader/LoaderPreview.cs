using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Client.Loader;

/// <summary>
/// Development aid, since 0.9.3: <c>UltimateRDP.exe --render-loader &lt;folder&gt;</c> writes PNG
/// previews of the loader with the paint code of the window, into bitmaps, without showing any
/// window. Each preview puts the loader on a plain background with the rounded corners of
/// Windows 11 and a soft shadow, which Windows draws for the real window and which are drawn here
/// to show the whole look. The texts are in French; a file <c>timings.txt</c> gives the time from
/// the start of the process to the first preview painted, which measures the start of the
/// runtime, of Windows Forms and of the paint code, without a window.
/// </summary>
internal static class LoaderPreview
{
    /// <summary>The switch of the command line. Not shown by --help: it is not for users.</summary>
    public const string Switch = "--render-loader";

    public static int Render(string folder)
    {
        Directory.CreateDirectory(folder);
        var culture = CultureInfo.GetCultureInfo("fr");
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        long? first = null;
        var written = new List<string>();
        foreach (var scale in new[] { 1f, 2f })
        {
            foreach (var theme in new[] { LoaderTheme.Light, LoaderTheme.Dark })
            {
                foreach (var (name, progress, slow) in Starts())
                {
                    var visual = LoaderForm.Describe(progress, canCancel: true, cancelHot: false, phase: 0.45, elapsedMilliseconds: slow ? LoaderProgress.SlowNoticeMilliseconds : 0);
                    var file = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"loader-{name}-{(theme.IsDark ? "dark" : "light")}-{scale * 100:0}.png"));
                    using (var bitmap = Draw(scale, theme, visual))
                    {
                        bitmap.Save(file, ImageFormat.Png);
                    }

                    first ??= (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
                    written.Add(Path.GetFileName(file));
                }
            }
        }

        File.WriteAllText(
            Path.Combine(folder, "timings.txt"),
            string.Create(CultureInfo.InvariantCulture, $"First preview painted and written {first} ms after the process started.\r\nFiles: {string.Join(", ", written)}\r\n"));
        return 0;
    }

    private static IEnumerable<(string Name, LoaderProgress Progress, bool Slow)> Starts()
    {
        var starting = new LoaderProgress(0);
        yield return ("starting", starting, false);

        var remoteApp = new LoaderProgress(0);
        remoteApp.FileRead("server.example.com", "Bloc-notes");
        remoteApp.On(LoaderEvent.Connecting);
        remoteApp.On(LoaderEvent.Connected);
        remoteApp.On(LoaderEvent.LoggedOn);
        yield return ("remoteapp", remoteApp, false);

        // The longest names: the brand line stays whole, the name of what opens ends with an ellipsis.
        var longNames = new LoaderProgress(0);
        longNames.FileRead("serveur-de-production-principal-du-siege.example.com", "Gestion commerciale et comptabilité générale intégrée");
        longNames.On(LoaderEvent.Connecting);
        yield return ("remoteapp-long", longNames, false);

        var desktop = new LoaderProgress(0);
        desktop.FileRead("server.example.com", null);
        desktop.On(LoaderEvent.Connecting);
        yield return ("desktop", desktop, false);

        var slow = new LoaderProgress(0);
        slow.FileRead("server.example.com", null);
        slow.On(LoaderEvent.Connecting);
        slow.On(LoaderEvent.Connected);
        yield return ("desktop-slow", slow, true);
    }

    /// <summary>The loader at <paramref name="scale"/>, on a background, with rounded corners and a shadow.</summary>
    public static Bitmap Draw(float scale, LoaderTheme theme, LoaderVisual visual)
    {
        var size = new Size((int)Math.Round(LoaderPainter.LogicalSize.Width * scale), (int)Math.Round(LoaderPainter.LogicalSize.Height * scale));
        using var fonts = new LoaderFonts(scale);
        using var loader = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        // Opaque, as the window: GDI text on a bitmap with alpha would lose its ClearType edges.
        using (var graphics = Graphics.FromImage(loader))
        {
            LoaderPainter.Paint(graphics, size, theme, visual, fonts);
        }

        var margin = (int)(32 * scale);
        var canvas = new Bitmap(size.Width + (2 * margin), size.Height + (2 * margin), PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(theme.IsDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(222, 230, 238));
            var radius = 8 * scale;
            var bounds = new RectangleF(margin, margin, size.Width, size.Height);
            for (var step = 12; step >= 1; step--)
            {
                var spread = step * scale;
                var shadow = new RectangleF(bounds.X - spread, bounds.Y - spread + (4 * scale), bounds.Width + (2 * spread), bounds.Height + (2 * spread));
                using var path = LoaderPainter.RoundedRectangle(shadow, radius + spread);
                using var brush = new SolidBrush(Color.FromArgb(theme.IsDark ? 9 : 5, 0, 0, 0));
                graphics.FillPath(brush, path);
            }

            using var clip = LoaderPainter.RoundedRectangle(bounds, radius);
            graphics.SetClip(clip);
            graphics.DrawImage(loader, margin, margin);
        }

        return canvas;
    }
}
