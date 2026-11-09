namespace UltimateRDP.Core.Protocol;

/// <summary>
/// Values shared by the client and the agent.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>
    /// Protocol version sent in every <c>hello</c> message.
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Name of the static virtual channel. Six characters, within the maximum of seven for a static channel.
    /// </summary>
    public const string ChannelName = "ULTRDP";
}
