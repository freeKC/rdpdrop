using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Client.Sessions;

namespace UltimateRDP.Client;

/// <summary>
/// File menu of the main form: open, recent connections, tabs, log off, saved credentials
/// and Quit.
/// </summary>
internal sealed partial class MainForm
{
    private void InitializeFileMenu()
    {
        _fileMenu.OpenClicked += (_, _) => OpenFromDialog();
        _fileMenu.RecentChosen += (_, path) => OpenRecent(path);
        _fileMenu.ClearRecentClicked += (_, _) => _recent.Clear();
        _fileMenu.ReopenClicked += (_, _) => ReopenActive();
        _fileMenu.CloseTabClicked += (_, _) => CloseActiveTab();
        _fileMenu.LogOffClicked += (_, _) => LogOffActiveSession();
        _fileMenu.ForgetCredentialsClicked += (_, _) => ForgetSavedCredentials();
        _fileMenu.QuitClicked += (_, _) => Close();
        _fileMenu.Root.DropDownOpening += (_, _) => OnFileMenuOpening();
        _recent.Changed += (_, _) => _fileMenu.ShowRecent(_recent.Paths);
        _fileMenu.ShowRecent(_recent.Paths);
    }

    private void OnFileMenuOpening()
    {
        UpdateMenu();
        var session = ActiveSession;
        _fileMenu.ShowSavedCredentials(session?.Host, session?.FindSavedCredentials() is { } kinds && kinds != SavedCredentialKinds.None);
    }

    private void ForgetSavedCredentials()
    {
        if (ActiveSession is not { } session)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            LocalText.Of(nameof(Strings.ForgetCredentialsQuestion), session.Host).ToString(),
            AppInfo.Name,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes)
        {
            ClientLog.Info($"Forgetting the saved credentials for {session.Host}.");
            session.ForgetSavedCredentials();
        }
    }

    /// <summary>
    /// Logs off the Windows session of the active tab, after a confirmation. Quit and
    /// Close tab only disconnect, like the Microsoft client; this ends the session.
    /// </summary>
    private void LogOffActiveSession()
    {
        if (ActiveSession is not { CanLogOff: true } session)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            Strings.LogOffQuestion,
            AppInfo.Name,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            ClientLog.Info($"Log off of {session.Host} not confirmed.");
            return;
        }

        session.LogOff();
        UpdateMenu();
    }

    /// <summary>
    /// Ctrl+O and Ctrl+W. While the session has the keyboard they belong to the remote
    /// computer, like every other key, and the menu is the way to reach the command.
    /// </summary>
    private bool ProcessFileShortcut(Keys keyData)
    {
        if (keyData is not ((Keys.Control | Keys.O) or (Keys.Control | Keys.W)))
        {
            return false;
        }

        if (ActiveSession?.HasSessionKeyboard == true)
        {
            return false;
        }

        ClientLog.Info($"Shortcut {(keyData == (Keys.Control | Keys.O) ? "Ctrl+O" : "Ctrl+W")}");
        if (keyData == (Keys.Control | Keys.O))
        {
            OpenFromDialog();
        }
        else
        {
            CloseActiveTab();
        }

        return true;
    }
}
