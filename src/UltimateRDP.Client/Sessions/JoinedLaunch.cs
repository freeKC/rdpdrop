using UltimateRDP.Core.RdpFiles;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// A RemoteApp launch that runs its program on the connection of another tab with the same
/// target, since 0.10.0, instead of opening a connection that would take the session of that
/// tab. It keeps its source, so that it can open its own connection if the shared one ends
/// before its program showed.
/// </summary>
/// <param name="launch">The program to start.</param>
/// <param name="source">The file or options it came from.</param>
/// <param name="askCredentials">What the command line asked for the password, for a connection of its own.</param>
internal sealed class JoinedLaunch(RemoteAppLaunch launch, SessionSource source, bool askCredentials)
{
    public RemoteAppLaunch Launch { get; } = launch;

    public SessionSource Source { get; } = source;

    public bool AskCredentials { get; } = askCredentials;

    /// <summary>The start of its program on the shared connection, as its loader follows it.</summary>
    public JoinedProgramStart Start { get; } = new();

    public string Name => Launch.DisplayName;

    public override string ToString() => $"{Name} ({Source})";
}
