namespace RdpDrop.Client.Sessions;

/// <summary>
/// Connection state of a <see cref="SessionView"/>.
/// </summary>
internal enum SessionState
{
    Disconnected,
    Connecting,
    Connected,
}
