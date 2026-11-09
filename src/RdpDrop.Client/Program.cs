using RdpDrop.Client.Localization;
using RdpDrop.Client.Logging;
using RdpDrop.Client.Resources;
using RdpDrop.Client.Settings;

namespace RdpDrop.Client;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportUnhandled(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportUnhandled(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        ClientLog.Info($"RdpDrop client {AppInfo.Version} starting, arguments: {string.Join(' ', args)}");

        // The language comes first: the open dialog below already shows texts.
        var settings = ClientSettings.Load(ClientSettings.DefaultPath);
        UiLanguage.Apply(settings.Language);

        // Without an argument the open dialog comes first. When it is
        // cancelled the window opens empty, so that File, Recent is still at hand.
        var path = args.Length > 0 ? args[0] : MainForm.AskForRdpFile(null);
        if (path is null)
        {
            ClientLog.Info("No .rdp file chosen, the window opens empty.");
        }

        Application.Run(new MainForm(path, settings));
        ClientLog.Info("RdpDrop client stopped.");
        return 0;
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
