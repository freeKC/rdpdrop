using RdpDrop.Client.Channel;
using RdpDrop.Client.Localization;
using RdpDrop.Client.Logging;
using RdpDrop.Client.Resources;
using RdpDrop.Core.Protocol;

namespace RdpDrop.Client.Sessions;

/// <summary>
/// Log off of the Windows session on the server, through the agent. Disconnect, Close
/// tab and Quit leave the session running on the server, like the Microsoft client; this
/// is the way to end it.
/// </summary>
/// <remarks>
/// The agent answers logoffResult at once, then asks Windows to log off. The control then
/// reports the disconnection as usual. Without an answer within five seconds the request
/// is reported as lost.
/// </remarks>
internal sealed partial class SessionView
{
    private const int LogOffAnswerTimeoutMilliseconds = 5000;

    private readonly System.Windows.Forms.Timer _logOffTimer = new() { Interval = LogOffAnswerTimeoutMilliseconds };

    /// <summary>Id of the last request, to match its results.</summary>
    private string? _logOffId;

    /// <summary>True from the request until its first answer or the timeout.</summary>
    private bool _logOffWaiting;

    /// <summary>True when the log off entry makes sense: connected, agent ready, no request waiting.</summary>
    public bool CanLogOff => IsConnected && !_closeRequested && _agent.State == AgentState.Ready && !_logOffWaiting;

    /// <summary>Sends the log off request. The caller has asked the user to confirm.</summary>
    public void LogOff()
    {
        if (!CanLogOff)
        {
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        ClientLog.Info($"Log off of the session on {Host} requested, id {id}.");
        if (!_agent.SendLogoff(new LogoffMessage(id)))
        {
            ShowDropStatus(LocalText.Of(nameof(Strings.LogOffNotSent)));
            return;
        }

        _logOffId = id;
        _logOffWaiting = true;
        _logOffTimer.Stop();
        _logOffTimer.Start();
        ShowDropStatus(LocalText.Of(nameof(Strings.LoggingOff)));
        RaiseStateChanged();
    }

    private void InitializeLogOff()
    {
        _logOffTimer.Tick += (_, _) => OnLogOffTimeout();
        _agent.LogoffResultReceived += (_, result) => OnLogOffResult(result);
    }

    private void DisposeLogOff() => _logOffTimer.Dispose();

    /// <summary>
    /// An accepted result keeps the "logging off" text until the control reports the
    /// disconnection; a refusal, which can follow an accept when Windows refused, shows
    /// the reason given by the agent.
    /// </summary>
    private void OnLogOffResult(LogoffResultMessage result)
    {
        if (_logOffId is null || result.Id != _logOffId)
        {
            ClientLog.Info($"Log off result {result.Id} matches no request of this session. Ignored.");
            return;
        }

        _logOffTimer.Stop();
        _logOffWaiting = false;
        if (result.Accepted)
        {
            ShowDropStatus(LocalText.Of(nameof(Strings.LoggingOff)));
        }
        else
        {
            _logOffId = null;
            ShowDropStatus(string.IsNullOrWhiteSpace(result.Message)
                ? LocalText.Of(nameof(Strings.LogOffFailedNoDetail))
                : LocalText.Of(nameof(Strings.LogOffFailedReason), result.Message));
        }

        RaiseStateChanged();
    }

    private void OnLogOffTimeout()
    {
        _logOffTimer.Stop();
        if (!_logOffWaiting)
        {
            return;
        }

        ClientLog.Warn($"No logoffResult for {_logOffId} within {LogOffAnswerTimeoutMilliseconds} ms.");
        _logOffWaiting = false;
        _logOffId = null;
        ShowDropStatus(LocalText.Of(nameof(Strings.LogOffNoAnswer)));
        RaiseStateChanged();
    }

    /// <summary>
    /// Called on the disconnection: the request is over. The connection label shows the
    /// text of the control, so the "logging off" text goes away.
    /// </summary>
    private void EndLogOff()
    {
        _logOffTimer.Stop();
        if (_logOffId is not null)
        {
            ClientLog.Info($"Session on {Host} ended after the log off request {_logOffId}.");
            ShowDropStatus(LocalText.Empty);
        }

        _logOffId = null;
        _logOffWaiting = false;
    }
}
