using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Settings;
using UltimateRDP.Core.Recent;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// The list of recently opened .rdp files, kept in the client settings and saved after
/// every change. The rules of the list itself live in <see cref="RecentList"/>.
/// </summary>
internal sealed class RecentConnections
{
    private readonly ClientSettings _settings;
    private readonly string _settingsPath;

    public RecentConnections(ClientSettings settings, string settingsPath)
    {
        _settings = settings;
        _settingsPath = settingsPath;
    }

    /// <summary>Raised after every change of <see cref="Paths"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>Full paths, most recent first.</summary>
    public IReadOnlyList<string> Paths => _settings.RecentFiles;

    /// <summary>Puts <paramref name="path"/> at the top of the list.</summary>
    public void Add(string path) => Replace(RecentList.Add(_settings.RecentFiles, path));

    /// <summary>Takes <paramref name="path"/> out of the list, for a file that could not be opened.</summary>
    public void Remove(string path)
    {
        if (_settings.RecentFiles.Contains(path, RecentList.PathComparer))
        {
            ClientLog.Info($"Removing {path} from the recent connections.");
            Replace(RecentList.Remove(_settings.RecentFiles, path));
        }
    }

    public void Clear()
    {
        ClientLog.Info("Clearing the recent connections.");
        Replace([]);
    }

    private void Replace(IReadOnlyList<string> paths)
    {
        _settings.RecentFiles = [.. paths];
        _settings.Save(_settingsPath);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
