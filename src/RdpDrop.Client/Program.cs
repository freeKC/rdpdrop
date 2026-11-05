using RdpDrop.Client.Logging;
using RdpDrop.Core.RdpFiles;

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

        var path = args.Length > 0 ? args[0] : AskForFile();
        if (path is null)
        {
            ClientLog.Info("No .rdp file chosen, exiting.");
            return 0;
        }

        path = Path.GetFullPath(path);
        RdpFile file;
        try
        {
            file = RdpFile.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ClientLog.Error($"Cannot read {path}", ex);
            MessageBox.Show($"Cannot read the file {path}.{Environment.NewLine}{ex.Message}", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(file.FullAddress))
        {
            ClientLog.Error($"{path} has no full address setting.");
            MessageBox.Show($"The file {path} does not name a computer to connect to (no \"full address\" line).", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        ClientLog.Info($"Using {path}");
        Application.Run(new MainForm(path, file));
        ClientLog.Info("RdpDrop client stopped.");
        return 0;
    }

    private static string? AskForFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open a Remote Desktop file",
            Filter = "Remote Desktop files (*.rdp)|*.rdp",
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
    }

    private static void ReportUnhandled(Exception? exception)
    {
        ClientLog.Error("Unhandled exception", exception);
        try
        {
            MessageBox.Show(
                $"An unexpected error occurred.{Environment.NewLine}{exception?.Message}{Environment.NewLine}{Environment.NewLine}Details are in the log folder {ClientLog.Directory}.",
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
