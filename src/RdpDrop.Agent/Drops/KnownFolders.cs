using RdpDrop.Agent.Interop;

namespace RdpDrop.Agent.Drops;

/// <summary>
/// Desktop and Downloads folders of the session user, including redirected ones.
/// </summary>
internal static class KnownFolders
{
    public static string Desktop
    {
        get
        {
            var path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return string.IsNullOrEmpty(path) ? Path.Combine(UserProfile, "Desktop") : path;
        }
    }

    public static string Downloads
    {
        get
        {
            var path = Shell32.GetKnownFolderPath(Shell32.FolderIdDownloads);
            return string.IsNullOrEmpty(path) ? Path.Combine(UserProfile, "Downloads") : path;
        }
    }

    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
