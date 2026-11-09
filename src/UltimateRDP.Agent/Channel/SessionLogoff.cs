using System.ComponentModel;
using System.Runtime.InteropServices;
using UltimateRDP.Agent.Interop;
using UltimateRDP.Agent.Localization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Resources;
using UltimateRDP.Core.Protocol;
using Message = UltimateRDP.Core.Protocol.Message;

namespace UltimateRDP.Agent.Channel;

/// <summary>
/// Logs off the session on a <c>logoff</c> request from the client: answers an accepted
/// <c>logoffResult</c> at once, waits a moment so that the answer reaches the client, then
/// calls ExitWindowsEx. When Windows refuses, a second <c>logoffResult</c> says why.
/// </summary>
/// <remarks>
/// Applications with unsaved work ask the user as Windows does; only applications that
/// do not answer are ended.
/// </remarks>
internal sealed class SessionLogoff
{
    /// <summary>Time given to the accepted answer to travel before the session closes.</summary>
    private static readonly TimeSpan AnswerDelay = TimeSpan.FromMilliseconds(500);

    private readonly FileLogger _logger;
    private readonly Func<Message, bool> _send;
    private readonly ClientLanguage _language;

    public SessionLogoff(FileLogger logger, Func<Message, bool> send, ClientLanguage language)
    {
        _logger = logger;
        _send = send;
        _language = language;
    }

    /// <summary>Handles a request. Called on the channel thread; returns at once.</summary>
    public void OnLogoff(LogoffMessage request)
    {
        _logger.Info($"Log off {request.Id}: requested by the client; the session logs off in {AnswerDelay.TotalMilliseconds:0} ms.");
        _send(new LogoffResultMessage(request.Id, true, LocalText.Of(nameof(Strings.LogoffAccepted)).In(_language.Culture)));

        var worker = new Thread(() => LogOffAfterDelay(request.Id))
        {
            IsBackground = true,
            Name = "UltimateRDP log off " + request.Id,
        };
        worker.Start();
    }

    private void LogOffAfterDelay(string id)
    {
        Thread.Sleep(AnswerDelay);
        if (User32.ExitWindowsEx(User32.EwxLogoff | User32.EwxForceIfHung, User32.ShtdnReasonMajorOther | User32.ShtdnReasonFlagPlanned))
        {
            _logger.Info($"Log off {id}: Windows accepted the log off.");
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        var reason = new Win32Exception(error).Message;
        _logger.Warn($"Log off {id}: ExitWindowsEx failed with error {error}, {reason}");
        _send(new LogoffResultMessage(id, false, LocalText.Of(nameof(Strings.LogoffFailed), reason).In(_language.Culture)));
    }
}
