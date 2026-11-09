namespace UltimateRDP.Core.Agent;

/// <summary>How the agent reads the data of the client from the virtual channel.</summary>
public enum ChannelReadMode
{
    /// <summary>WTSVirtualChannelRead with a short timeout. The default.</summary>
    Wts,

    /// <summary>
    /// ReadFile on the file handle of the channel, with a large buffer. An experiment: the
    /// agent goes back to <see cref="Wts"/> for the connection at the first inconsistency.
    /// </summary>
    File,
}

/// <summary>Reads and writes the <c>channelRead</c> setting of agent.json.</summary>
public static class ChannelReadModes
{
    /// <summary>
    /// Reads a setting value: "wts" or "file", in any case and with spaces around. A missing
    /// value gives <see cref="ChannelReadMode.Wts"/> and counts as recognized; any other
    /// value gives <see cref="ChannelReadMode.Wts"/> and <paramref name="recognized"/>
    /// false, so that the caller can log it.
    /// </summary>
    public static ChannelReadMode Parse(string? value, out bool recognized)
    {
        recognized = true;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "wts":
                return ChannelReadMode.Wts;

            case "file":
                return ChannelReadMode.File;

            default:
                recognized = false;
                return ChannelReadMode.Wts;
        }
    }

    /// <summary>The value written to agent.json: "wts" or "file".</summary>
    public static string ToSetting(ChannelReadMode mode) => mode == ChannelReadMode.File ? "file" : "wts";
}
