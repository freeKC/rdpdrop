using UltimateRDP.Agent.Channel;
using UltimateRDP.Agent.Drops;
using UltimateRDP.Agent.Localization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Settings;
using UltimateRDP.Core.Protocol;
using Message = UltimateRDP.Core.Protocol.Message;

namespace UltimateRDP.Agent;

/// <summary>
/// Everything that serves a connected client: the message handler, the drag
/// infrastructure, the staging folder and the log off. Created when the first message
/// arrives, so that a session that never sees an UltimateRDP client never builds it.
/// </summary>
internal sealed class AgentServices
{
    private readonly AgentProtocolHandler _handler;
    private readonly NativeDropCoordinator _native;
    private readonly StagingFolder _staging;

    /// <param name="logger">The log.</param>
    /// <param name="settings">The settings of the agent.</param>
    /// <param name="send">Sends a message to the client.</param>
    /// <param name="meter">Measures the channel during each transfer.</param>
    /// <param name="clientModel">The byte model detected on the data of the client, for the hello of the agent.</param>
    public AgentServices(FileLogger logger, AgentSettings settings, Func<Message, bool> send, ChannelMeter meter, Func<ChannelByteModel> clientModel)
    {
        _staging = new StagingFolder(logger, AgentInfo.StagingDirectory);
        var probe = new DriveRedirectionProbe(logger);
        var language = new ClientLanguage();
        _native = new NativeDropCoordinator(logger, send, settings, probe, _staging, language, meter);
        var logoff = new SessionLogoff(logger, send, language);
        _handler = new AgentProtocolHandler(logger, send, probe, _native, logoff, language, clientModel);
    }

    /// <summary>Handles one line from the client. Runs on the channel thread.</summary>
    public void Handle(string line) => _handler.Handle(line);

    /// <summary>
    /// Ends the drag in progress, waiting at most <paramref name="wait"/>, and stops the
    /// pending deletions of staged folders; the next start deletes them.
    /// </summary>
    public void Stop(TimeSpan wait)
    {
        _native.Stop(wait);
        _staging.Dispose();
    }
}
