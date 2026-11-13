using System.Globalization;
using UltimateRDP.Client.Loader;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Client.Sessions;
using UltimateRDP.Client.Settings;
using UltimateRDP.Core.Local;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Client;

internal static class Program
{
    /// <summary>The purpose of the pipe of the running client, in its name.</summary>
    private const string PipePurpose = "Client";

    /// <summary>Time a second start waits to reach the first one.</summary>
    private static readonly TimeSpan HandOffTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Time the first start waits for its window before it takes a file of a second start.</summary>
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(5);

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportUnhandled(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportUnhandled(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();

        // Development aid: previews of the loader as PNG files, without any window.
        if (args is [LoaderPreview.Switch, var previewFolder])
        {
            return LoaderPreview.Render(previewFolder);
        }

        // Before anything slower: the two settings the loader needs, the single instance, then
        // the loader itself, so that something shows at once after a click on a shortcut.
        var early = ReadEarlySettings();
        UiLanguage.Apply(early.Language, log: false);
        var options = LaunchOptions.Parse(args);
        if (options.Help)
        {
            MessageBox.Show(LaunchTexts.Help().ToString(), AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        var path = options.Error is null && options.Path is { } given ? FullPath(given) : null;
        var source = options.Error is null && options.HasConnection ? new SessionSource(path, options.Connection) : null;

        // One client per user and session: a second start hands its file to the first one,
        // which shows the loader of the new session itself.
        MainForm? form = null;
        using var formReady = new ManualResetEventSlim(false);
        var pipe = args is [DropProgressDemo.Switch] || options.Error is not null
            ? null
            : SameUserPipe.TryStart(PipePurpose, request => OnOpenRequest(request, () => form, formReady), line => ClientLog.Info(line));
        if (pipe is null && options.Error is null && args is not [DropProgressDemo.Switch] && HandOff(source, options))
        {
            return 0;
        }

        LoaderForm? loader = null;
        long? loaderMilliseconds = null;
        if (early.Loader && source is not null)
        {
            loader = new LoaderForm("the start");
            loader.ShowNow();
            loaderMilliseconds = MillisecondsSinceProcessStart();
        }

        ClientLog.Info($"UltimateRDP client {AppInfo.Version} starting, arguments: {string.Join(' ', args)}");
        if (loaderMilliseconds is { } shown)
        {
            ClientLog.Info(string.Create(CultureInfo.InvariantCulture, $"Loader shown {shown} ms after the process started."));
        }

        // The language comes first: the open dialog below already shows texts.
        var settings = ClientSettings.Load(ClientSettings.DefaultPath);
        ClientLog.DebugEnabled = settings.DebugLog;
        ClientLog.Info($"Settings: {settings}.");
        UiLanguage.Apply(settings.Language);

        // Development aid: the drop window with simulated drops, without a connection.
        if (args is [DropProgressDemo.Switch])
        {
            var demo = new MainForm(null, settings);
            demo.Shown += (_, _) => DropProgressDemo.Start(demo);
            Application.Run(demo);
            return 0;
        }

        if (options.Error is { } error)
        {
            ClientLog.Warn($"Command line refused: {error}");
            MessageBox.Show(LocalText.Of(nameof(Strings.CommandLineError), LaunchTexts.Error(error)).ToString(), AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        using (pipe)
        {
            // Without a file or a server the open dialog comes first. When it is
            // cancelled the window opens empty, so that File, Recent is still at hand.
            source ??= MainForm.AskForRdpFile(null) is { } chosen ? SessionSource.FromFile(chosen) : null;
            if (source is null)
            {
                ClientLog.Info("No .rdp file chosen, the window opens empty.");
            }

            var described = source is null ? null : MainForm.Describe(source);
            if (described is { } what)
            {
                loader?.FileRead(what.Server, what.Program);
            }

            var hidden = source is not null && options.StartsHidden(described?.Program is not null);
            form = new MainForm(source, settings, hidden, options.AskCredentials, loader);
            form.HandleCreated += (_, _) => formReady.Set();
            Application.Run(form);
            formReady.Reset();
        }

        ClientLog.Info("UltimateRDP client stopped.");
        return 0;
    }

    /// <summary>The settings the loader needs, read from client.json without its full reader; the defaults when it cannot be read.</summary>
    private static (bool Loader, string? Language) ReadEarlySettings()
    {
        try
        {
            return File.Exists(ClientSettings.DefaultPath) ? LoaderRules.ReadEarlySettings(File.ReadAllBytes(ClientSettings.DefaultPath)) : (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (true, null);
        }
    }

    private static long MillisecondsSinceProcessStart()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return (long)(DateTime.Now - process.StartTime).TotalMilliseconds;
    }

    private static string FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return path;
        }
    }

    /// <summary>Hands the file or the connection to the client already running for this user and session. True when it took it.</summary>
    private static bool HandOff(SessionSource? source, LaunchOptions options)
    {
        try
        {
            var request = new OpenRequest(source?.Path, options.Window, options.AskCredentials, source is null || source.Connection.IsEmpty ? null : source.Connection);
            var answer = LocalMessages.DecodeAnswer(SameUserPipe.Call(PipePurpose, LocalMessages.Encode(request), HandOffTimeout));
            if (answer is { Ok: true })
            {
                ClientLog.Info($"The client already running took {source?.ToString() ?? "the request to show its window"}; this start exits.");
                return true;
            }

            ClientLog.Info($"The client already running did not take the file ({answer?.Message ?? "no answer"}); this start runs on its own.");
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ClientLog.Info($"The client already running cannot be reached ({ex.Message}); this start runs on its own.");
        }

        return false;
    }

    /// <summary>A second start handed its file, on a thread of the pool: the window opens it, on its thread.</summary>
    private static byte[] OnOpenRequest(byte[] bytes, Func<MainForm?> form, ManualResetEventSlim ready)
    {
        if (LocalMessages.DecodeOpen(bytes) is not { } request)
        {
            ClientLog.Warn($"A request of {bytes.Length} bytes on the pipe of the client is not one; refused.");
            return LocalMessages.Encode(new LocalAnswer(false, "not a request"));
        }

        if (!ready.Wait(WindowWait) || form() is not { AcceptsRequests: true } target)
        {
            return LocalMessages.Encode(new LocalAnswer(false, "the window of the client is not there"));
        }

        try
        {
            target.BeginInvoke(() => target.OpenFromRequest(request));
            return LocalMessages.Encode(new LocalAnswer(true));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return LocalMessages.Encode(new LocalAnswer(false, "the window of the client is closing"));
        }
    }

    private static void ReportUnhandled(Exception? exception)
    {
        ClientLog.Error("Unhandled exception", exception);
        try
        {
            MessageBox.Show(
                LocalText.Of(nameof(Strings.UnexpectedError), exception?.Message, ClientLog.Directory).ToString(),
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (InvalidOperationException)
        {
            // No message loop left to show it; the log has it.
        }
    }
}
