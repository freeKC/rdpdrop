using System.Diagnostics.CodeAnalysis;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.RdpFiles;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// Reads a .rdp file for a new session and checks that it names a computer.
/// </summary>
internal static class RdpFileLoader
{
    /// <summary>
    /// Reads <paramref name="path"/>. On success returns true with the full path and the
    /// file; otherwise returns false with a message for the user, already logged.
    /// </summary>
    public static bool TryLoad(string path, out string fullPath, [NotNullWhen(true)] out RdpFile? file, out LocalText error)
    {
        fullPath = path;
        file = null;
        try
        {
            fullPath = Path.GetFullPath(path);
            file = RdpFile.Load(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ClientLog.Error($"Cannot read {path}", ex);
            error = LocalText.Of(nameof(Strings.CannotReadFile), path, ex.Message);
            return false;
        }

        if (string.IsNullOrWhiteSpace(file.FullAddress))
        {
            ClientLog.Error($"{fullPath} has no full address setting.");
            error = LocalText.Of(nameof(Strings.FileHasNoAddress), fullPath);
            file = null;
            return false;
        }

        RemoteAppLaunch.FromFile(file, out var rejection);
        if (rejection == RemoteAppRejection.NoProgram)
        {
            ClientLog.Error($"{fullPath} asks for RemoteApp (remoteapplicationmode:i:1) without remoteapplicationprogram or remoteapplicationfile.");
            error = LocalText.Of(nameof(Strings.FileHasNoRemoteProgram), fullPath);
            file = null;
            return false;
        }

        ClientLog.Info($"Using {fullPath}");
        error = LocalText.Empty;

        return true;
    }
}
