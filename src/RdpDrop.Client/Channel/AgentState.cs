namespace RdpDrop.Client.Channel;

/// <summary>
/// What the client knows about the agent in the remote session.
/// </summary>
internal enum AgentState
{
    /// <summary>No session.</summary>
    NotConnected,

    /// <summary>Connected; hello messages are being sent and no answer came yet.</summary>
    Waiting,

    /// <summary>The agent answered; drops can be sent.</summary>
    Ready,

    /// <summary>No answer during the discovery period.</summary>
    NotFound,
}
