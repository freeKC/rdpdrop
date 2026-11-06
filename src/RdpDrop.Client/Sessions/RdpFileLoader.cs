using System.Diagnostics.CodeAnalysis;
using RdpDrop.Client.Logging;
using RdpDrop.Core.RdpFiles;

namespace RdpDrop.Client.Sessions;

/// <summary>
/// Reads a .rdp file for a new session and checks that it names a computer.
/// </summary>
internal static class RdpFileLoader
{
    /// <summary>
    /// Reads <paramref name="path"/>. On success returns true with the full path and the
    /// file; otherwise returns false with a message for the user, already logged.
    /// </summary>
    public static bool TryLoad(string path, out string fullPath, [NotNullWhen(true)] out RdpFile? file, out string error)
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
            error = $"Cannot read the file {path}.{Environment.NewLine}{ex.Message}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(file.FullAddress))
        {
            ClientLog.Error($"{fullPath} has no full address setting.");
            error = $"The file {fullPath} does not name a computer to connect to (no \"full address\" line).";
            file = null;
            return false;
        }

        ClientLog.Info($"Using {fullPath}");
        error = string.Empty;
        return true;
    }
}
