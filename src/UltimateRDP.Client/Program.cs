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
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(15);

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportUnhandled(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportUnhandled(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        ClientLog.Info($"UltimateRDP client {AppInfo.Version} starting, arguments: {string.Join(' ', args)}");

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

        var options = LaunchOptions.Parse(args);
        if (options.Error is { } error)
        {
            ClientLog.Warn($"Command line refused: {error}");
            MessageBox.Show(LocalText.Of(nameof(Strings.CommandLineError), error).ToString(), AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        var path = options.Path is { } given ? FullPath(given) : null;

        // One client per user and session: a second start hands its file to the first one.
        MainForm? form = null;
        using var formReady = new ManualResetEventSlim(false);
        var pipe = SameUserPipe.TryStart(PipePurpose, request => OnOpenRequest(request, () => form, formReady), line => ClientLog.Info(line));
        if (pipe is null && HandOff(path, options))
        {
            return 0;
        }

        using (pipe)
        {
            // Without an argument the open dialog comes first. When it is
            // cancelled the window opens empty, so that File, Recent is still at hand.
            path ??= MainForm.AskForRdpFile(null);
            if (path is null)
            {
                ClientLog.Info("No .rdp file chosen, the window opens empty.");
            }

            var hidden = path is not null && options.StartsHidden(MainForm.IsRemoteAppFile(path));
            form = new MainForm(path, settings, hidden, options.AskCredentials);
            form.HandleCreated += (_, _) => formReady.Set();
            Application.Run(form);
            formReady.Reset();
        }

        ClientLog.Info("UltimateRDP client stopped.");
        return 0;
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

    /// <summary>Hands the file to the client already running for this user and session. True when it took it.</summary>
    private static bool HandOff(string? path, LaunchOptions options)
    {
        try
        {
            var answer = LocalMessages.DecodeAnswer(SameUserPipe.Call(PipePurpose, LocalMessages.Encode(new OpenRequest(path, options.Window, options.AskCredentials)), HandOffTimeout));
            if (answer is { Ok: true })
            {
                ClientLog.Info($"The client already running took {path ?? "the request to show its window"}; this start exits.");
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
