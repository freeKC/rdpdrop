using System.Drawing.Imaging;
using System.Globalization;
using UltimateRDP.Client.Launchers;
using UltimateRDP.Client.Loader;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Resources;
using UltimateRDP.Client.Sessions;
using UltimateRDP.Client.Settings;
using UltimateRDP.Core.Launchers;
using UltimateRDP.Core.RemoteApp;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Client.Previews;

/// <summary>
/// Development aid, since 0.10.0: <c>UltimateRDP.exe --render-readme &lt;folder&gt; [en fr de ...]</c>
/// draws the images of the README with the drawing code of the client, in windows that are never
/// shown, and only with sample data: server.example.com, the user "user" in the language of the
/// image, the program Notepad, sample files. With one language the images go into the folder;
/// with several, into a subfolder per language. Run it in a process that Windows starts without
/// DPI awareness (<c>__COMPAT_LAYER=DpiUnaware</c>) so that everything is at 100 %. The switch is
/// not in the help of the command line.
/// </summary>
internal static class ReadmeImages
{
    /// <summary>The switch of the command line.</summary>
    public const string Switch = "--render-readme";

    private const string Server = "server.example.com";
    private const string Program = "Notepad";

    private static readonly Dictionary<string, (string User, string Placeholder)> Samples = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = ("user", "Sample image: the remote desktop of server.example.com shows here."),
        ["fr"] = ("utilisateur", "Image d'exemple : le bureau à distance de server.example.com s'affiche ici."),
        ["de"] = ("benutzer", "Beispielbild: hier erscheint der Remotedesktop von server.example.com."),
        ["es"] = ("usuario", "Imagen de ejemplo: aquí se muestra el escritorio remoto de server.example.com."),
        ["it"] = ("utente", "Immagine di esempio: qui appare il desktop remoto di server.example.com."),
        ["nl"] = ("gebruiker", "Voorbeeldafbeelding: hier verschijnt het externe bureaublad van server.example.com."),
        ["pt"] = ("usuario", "Imagem de exemplo: aqui aparece a área de trabalho remota de server.example.com."),
    };

    public static int Render(string folder, IReadOnlyList<string> languages)
    {
        var chosen = languages.Count == 0 ? ["en"] : languages;
        var notes = string.Empty;
        foreach (var language in chosen)
        {
            var target = chosen.Count == 1 ? folder : Path.Combine(folder, language);
            Directory.CreateDirectory(target);
            UiLanguage.Apply(language, log: false);

            // Numbers as the readers of that language write them.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
            var (user, placeholder) = Samples.TryGetValue(language, out var sample) ? sample : Samples["en"];
            var exe = $@"C:\Users\{user}\AppData\Local\UltimateRDP\client-{AppInfo.Version}\UltimateRDP.exe";

            foreach (var theme in new[] { LoaderTheme.Light, LoaderTheme.Dark })
            {
                var themeName = theme.IsDark ? "dark" : "light";
                Save(target, $"loader-remoteapp-{themeName}", LoaderPreview.Draw(1f, theme, Loader(remoteApp: true)));
                Save(target, $"loader-desktop-{themeName}", LoaderPreview.Draw(1f, theme, Loader(remoteApp: false)));
            }

            var draft = new LauncherDraft { Server = Server, User = user, RemoteApp = true, Program = "||" + Program, DisplayName = Program };
            Save(target, "launcher-short", LauncherPreview.Draw(1f, draft, advanced: false, exe, ref notes));
            var advanced = draft with
            {
                AskPassword = true,
                Clipboard = true,
                Drives = true,
                ExeKind = LauncherExeKind.WithVariables,
                TypedExe = $@"%LOCALAPPDATA%\UltimateRDP\client-{AppInfo.Version}\UltimateRDP.exe",
            };
            Save(target, "launcher-advanced", LauncherPreview.Draw(1f, advanced, advanced: true, exe, ref notes));

            Save(target, "transfer-progress", Transfer(done: false));
            Save(target, "transfer-done", Transfer(done: true));
            Save(target, "receive-progress", Receive(user, done: false));
            Save(target, "receive-done", Receive(user, done: true));
            Save(target, "main-window-file-menu", MainWindow(placeholder, remoteApp: false));
            Save(target, "main-window-remoteapp", MainWindow(placeholder, remoteApp: true));
            Save(target, "client-tray-menu", TrayMenu());
        }

        if (notes.Length > 0)
        {
            File.WriteAllText(Path.Combine(folder, "notes.txt"), notes + "\r\n");
        }

        return 0;
    }

    private static void Save(string folder, string name, Bitmap image)
    {
        using (image)
        {
            image.Save(Path.Combine(folder, name + ".png"), ImageFormat.Png);
        }
    }

    private static LoaderVisual Loader(bool remoteApp)
    {
        var progress = new LoaderProgress(0);
        progress.FileRead(Server, remoteApp ? Program : null);
        progress.On(LoaderEvent.Connecting);
        if (remoteApp)
        {
            progress.On(LoaderEvent.Connected);
            progress.On(LoaderEvent.LoggedOn);
        }

        return LoaderForm.Describe(progress, canCancel: true, cancelHot: false, phase: 0.45, elapsedMilliseconds: 0);
    }

    /// <summary>A large drop through the direct connection: 4.2 GB in three files, at the rate measured in production.</summary>
    private static Bitmap Transfer(bool done)
    {
        using var window = new DropProgressForm("sample", Server);
        long[] sizes = [1_200_000_000, 2_400_000_000, 600_000_000];
        var total = sizes.Sum();
        var progress = new TransferProgress(total, 0);
        window.ShowPreparing("Video", 3);
        window.ShowDirect(true);
        var doneBytes = done ? total : 1_680_000_000L;
        progress.Report(5000, doneBytes - 1_400_000_000L);
        progress.Report(6000, doneBytes - 1_120_000_000L);
        progress.Report(10_000, doneBytes);
        var index = TransferProgress.CurrentFile(sizes, doneBytes);
        window.ShowProgress($@"Video\part{index + 1}.mp4", index + 1, sizes.Length, progress);
        if (done)
        {
            window.ShowTransferred(sizes.Length, TimeSpan.FromSeconds(15), TransferProgressText.MegabytesPerSecond(total, 15));
            window.ShowDropResult(LocalText.Of(nameof(Strings.DroppedOn), LocalText.Raw("Explorer"), LocalText.Of(nameof(Strings.EffectCopy))), failed: false);
        }

        return Window(window);
    }

    /// <summary>Files sent from the session to the Downloads folder of the sample user.</summary>
    private static Bitmap Receive(string user, bool done)
    {
        var folder = $@"C:\Users\{user}\Downloads";
        using var window = DropProgressForm.ForReceive("sample", folder);
        long[] sizes = [850_000_000, 420_000_000, 2_000_000];
        var total = sizes.Sum();
        var progress = new TransferProgress(total, 0);
        window.ShowPreparing("export.zip", 3);
        window.ShowDirect(true);
        var doneBytes = done ? total : 640_000_000L;
        progress.Report(2000, doneBytes - 500_000_000L);
        progress.Report(6000, doneBytes);
        var index = TransferProgress.CurrentFile(sizes, doneBytes);
        string[] names = ["export.zip", "photos.zip", "notes.txt"];
        window.ShowProgress(names[index], index + 1, sizes.Length, progress);
        if (done)
        {
            window.ShowReceived(sizes.Length, TimeSpan.FromSeconds(11), TransferProgressText.MegabytesPerSecond(total, 11), LocalText.Of(nameof(Strings.ReceiveWhereMany), sizes.Length, folder), () => { });
        }

        return Window(window);
    }

    private static Bitmap Window(Form window)
    {
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-32000, -32000);
        using var client = HiddenRender.DrawClient(window);
        return HiddenRender.Frame(client, window.Text, window.Font, window.DeviceDpi / 96f);
    }

    private static Bitmap MainWindow(string placeholderText, bool remoteApp)
    {
        using var form = new MainForm(null, new ClientSettings());
        Control content;
        string title;
        if (remoteApp)
        {
            var panel = new RemoteAppPanel();
            panel.ShowState(
                Program,
                LocalText.Of(nameof(Strings.RemoteAppHint), Program).ToString(),
                LocalText.Of(nameof(Strings.RemoteAppWindowCount), 1).ToString(),
                LocalText.Of(nameof(Strings.ConnectedTo), Server).ToString(),
                LocalText.Of(nameof(Strings.AgentReady), AppInfo.Version).ToString(),
                string.Empty,
                canDisconnect: true);
            content = panel;
            title = $"{AppInfo.Name}: {Program} ({Server})";
        }
        else
        {
            content = new Label
            {
                Text = placeholderText,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(58, 96, 128),
                ForeColor = Color.White,
            };
            title = $"{AppInfo.Name}: {Server}";
        }

        var drop = remoteApp ? string.Empty : LocalText.Of(nameof(Strings.DroppedOn), LocalText.Raw("Explorer"), LocalText.Of(nameof(Strings.EffectCopy))).ToString();
        return form.DrawSample(content, remoteApp ? Program : Server, title, LocalText.Of(nameof(Strings.ConnectedTo), Server).ToString(), LocalText.Of(nameof(Strings.AgentReady), AppInfo.Version).ToString(), drop, openFileMenu: !remoteApp, new Size(860, 540));
    }

    private static Bitmap TrayMenu()
    {
        using var menu = new ContextMenuStrip();
        using var bold = new Font(menu.Font, FontStyle.Bold);
        ClientTray.Fill(menu.Items, bold, [(Program, Server)], () => { }, () => { }, () => { });
        using var image = HiddenRender.DrawDropDown(menu);
        return HiddenRender.OnBackground(image, 12, Color.FromArgb(222, 230, 238));
    }
}
