using System.Diagnostics.CodeAnalysis;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Launchers;
using UltimateRDP.Core.RdpFiles;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// Reads a .rdp file for a new session, with the options of the command line on top of it or
/// alone, and checks that the result names a computer.
/// </summary>
internal static class RdpFileLoader
{
    /// <summary>
    /// Reads <paramref name="path"/>. On success returns true with the full path and the
    /// file; otherwise returns false with a message for the user, already logged.
    /// </summary>
    public static bool TryLoad(string path, out string fullPath, [NotNullWhen(true)] out RdpFile? file, out LocalText error) =>
        TryLoad(SessionSource.FromFile(path), out fullPath, out file, out error);

    /// <summary>
    /// Reads <paramref name="source"/>: the text of its file, if any, followed by the lines of
    /// its options, through the same parser (<see cref="ConnectionOptions.ToRdpContent"/>), so
    /// that the options win over the file. On success returns true with the full path of the
    /// file (or the server when there is no file) and the result.
    /// </summary>
    public static bool TryLoad(SessionSource source, out string fullPath, [NotNullWhen(true)] out RdpFile? file, out LocalText error)
    {
        var path = source.Path;
        fullPath = path ?? source.Connection.Server ?? string.Empty;
        file = null;
        try
        {
            string? content = null;
            if (path is not null)
            {
                fullPath = Path.GetFullPath(path);
                content = RdpFile.LoadText(fullPath);
            }

            file = RdpFile.Parse(source.Connection.IsEmpty ? content ?? string.Empty : source.Connection.ToRdpContent(content));
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

        ClientLog.Info($"Using {source}");
        error = LocalText.Empty;

        return true;
    }
}
