using System.Runtime.InteropServices;

namespace UltimateRDP.Client.Settings;

/// <summary>Folders of the user that .NET does not name.</summary>
internal static class KnownFolders
{
    /// <summary>FOLDERID_Downloads.</summary>
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The Downloads folder of the user, where Explorer has it; the folder Downloads of the profile when Windows does not say.</summary>
    public static string Downloads()
    {
        if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var path) == 0 && !string.IsNullOrEmpty(path))
        {
            return path;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszPath);
}
