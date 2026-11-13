using UltimateRDP.Core.Launchers;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// What a session is opened from: a .rdp file, the options of a connection given on the
/// command line (since 0.10.0), or both, the options on top of the file. The session reads it
/// again when it is opened again.
/// </summary>
/// <param name="Path">The .rdp file, or null.</param>
/// <param name="Connection">The options of the command line; <see cref="ConnectionOptions.None"/> without any.</param>
/// <param name="Trusted"><c>--trusted</c> on the command line, since 0.10.0: no warning of Windows about the publisher and the shared resources.</param>
internal sealed record SessionSource(string? Path, ConnectionOptions Connection, bool Trusted = false)
{
    /// <summary>A session opened from a .rdp file only.</summary>
    public static SessionSource FromFile(string path) => new(path, ConnectionOptions.None);

    /// <summary>True for a .rdp file without options: it goes to the recent connections.</summary>
    public bool IsPlainFile => Path is not null && Connection.IsEmpty;

    /// <summary>The source in the log: the path, the server, or both.</summary>
    public override string ToString() => (Path, Connection.IsEmpty) switch
    {
        (null, _) => $"the command line (server {Connection.Server})",
        (_, true) => Path,
        _ => $"{Path} with options of the command line",
    };
}
