using System.Drawing.Imaging;
using System.Globalization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Settings;

namespace UltimateRDP.Agent.Tray;

/// <summary>
/// Development aid, since 0.10.0: <c>UltimateRDP.Agent.exe --render-tray-menu &lt;folder&gt; [en fr ...]</c>
/// draws the menu of the notification icon into <c>agent-tray-menu.png</c>, with the default
/// settings and "Send files to my PC..." enabled, as in a session connected with UltimateRDP,
/// for the README. Nothing is shown: the icon is never made visible and the menu never opens.
/// With several languages the images go into a subfolder per language. Not in the help.
/// </summary>
internal static class TrayMenuImage
{
    public const string Switch = "--render-tray-menu";

    public static int Render(string folder, IReadOnlyList<string> languages)
    {
        var chosen = languages.Count == 0 ? ["en"] : languages;
        var logs = Directory.CreateTempSubdirectory("ultimaterdp-agent-render-");
        try
        {
            var logger = new FileLogger(logs.FullName);
            foreach (var language in chosen)
            {
                var culture = CultureInfo.GetCultureInfo(language);
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.CurrentCulture = culture;
                var target = chosen.Count == 1 ? folder : Path.Combine(folder, language);
                Directory.CreateDirectory(target);
                var settings = AgentSettings.Load(Path.Combine(logs.FullName, "none.json"), logger, Path.Combine(logs.FullName, "none-machine.json"));
                using var tray = new AgentTrayIcon(logger, settings, () => { }, _ => { }, () => { });
                tray.SetCanSend(true);
                var menu = tray.Menu;
                _ = menu.Handle;
                menu.PerformLayout();
                menu.Size = menu.GetPreferredSize(Size.Empty);
                menu.PerformLayout();
                using var image = new Bitmap(menu.Width, menu.Height, PixelFormat.Format24bppRgb);
                menu.DrawToBitmap(image, new Rectangle(Point.Empty, menu.Size));
                const int margin = 12;
                using var canvas = new Bitmap(image.Width + (2 * margin), image.Height + (2 * margin), PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(canvas))
                {
                    graphics.Clear(Color.FromArgb(222, 230, 238));
                    graphics.DrawImage(image, margin, margin);
                }

                canvas.Save(Path.Combine(target, "agent-tray-menu.png"), ImageFormat.Png);
            }

            return 0;
        }
        finally
        {
            try
            {
                logs.Delete(recursive: true);
            }
            catch (IOException)
            {
                // The log file may still be open; the temporary folder is left.
            }
        }
    }
}
