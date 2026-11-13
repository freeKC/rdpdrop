using System.Globalization;
using UltimateRDP.Agent.Direct;
using UltimateRDP.Agent.Localization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Resources;
using UltimateRDP.Agent.Settings;
using UltimateRDP.Core.Local;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;
using Message = UltimateRDP.Core.Protocol.Message;

namespace UltimateRDP.Agent.Send;

/// <summary>
/// Sends files of the session to the computer of the client, since 0.9.0. A request comes
/// from the Send To shortcut through <c>--send</c>, or from the tray menu: the coordinator
/// checks that the setting allows it, that the client said in its hello that it receives
/// files, and that no other direct transfer runs in the session; lists the files; opens a
/// <see cref="DirectSendListener"/>; sends the sendOffer through the channel; and waits for
/// the sendAck of the client, so that the request can say at once whether the client took
/// the files. Then the client connects, and the transfer runs on the threads of the listener.
/// </summary>
/// <remarks>Thread safe: requests come from the pool, the answers of the client from the channel thread.</remarks>
internal sealed class SendCoordinator
{
    /// <summary>The client answers an offer within this time, or the send is given up.</summary>
    public static readonly TimeSpan AckWait = TimeSpan.FromSeconds(8);

    private readonly FileLogger _logger;
    private readonly AgentSettings _settings;
    private readonly Func<Message, bool> _send;
    private readonly Func<bool> _dropUsesDirect;
    private readonly Lock _gate = new();
    private bool _clientAccepts;
    private Pending? _current;

    /// <param name="logger">The log.</param>
    /// <param name="settings">The settings of the agent.</param>
    /// <param name="send">Sends a message to the client.</param>
    /// <param name="dropUsesDirect">True while a drop uses the direct connection of the session.</param>
    public SendCoordinator(FileLogger logger, AgentSettings settings, Func<Message, bool> send, Func<bool> dropUsesDirect)
    {
        _logger = logger;
        _settings = settings;
        _send = send;
        _dropUsesDirect = dropUsesDirect;
    }

    /// <summary>Raised, on the thread that learnt it, when a client that receives files comes or goes.</summary>
    public event Action<bool>? ClientAcceptsChanged;

    /// <summary>True while the connected client receives files and the setting allows it.</summary>
    public bool CanSend
    {
        get
        {
            lock (_gate)
            {
                return _clientAccepts && _settings.SendToClient;
            }
        }
    }

    /// <summary>True while a send is offered or runs: it uses the direct connection of the session.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _current is { Listener.IsActive: true };
            }
        }
    }

    /// <summary>The hello of the client said whether it receives files. Channel thread.</summary>
    public void OnHello(bool acceptsFiles) => SetClientAccepts(acceptsFiles, "the hello of the client");

    /// <summary>The client disconnected: a send in progress stops.</summary>
    public void OnClientGone()
    {
        Pending? current;
        lock (_gate)
        {
            current = _current;
        }

        current?.Listener.Close("the client disconnected");
        SetClientAccepts(false, "the client disconnected");
    }

    /// <summary>The agent stops: a send in progress stops.</summary>
    public void Stop()
    {
        Pending? current;
        lock (_gate)
        {
            current = _current;
            _clientAccepts = false;
        }

        current?.Listener.Close("the agent stops");
    }

    /// <summary>
    /// Sends <paramref name="paths"/>, files or folders, to the client. Blocks until the client
    /// answered the offer, at most <see cref="AckWait"/>, so that the answer says whether it took
    /// the files; the text of a refusal is in the language of the session, for the user of the
    /// session. Callable from any thread but the channel thread, which brings the answer.
    /// </summary>
    public LocalAnswer Request(IReadOnlyList<string> paths, string source)
    {
        _logger.Info(string.Create(CultureInfo.InvariantCulture, $"Send requested from {source} with {paths.Count} item(s), the first one {paths.FirstOrDefault() ?? "(none)"}."));
        if (!_settings.SendToClient)
        {
            return Refuse(LocalText.Of(nameof(Strings.SendOff)), "the setting sendToClient is never");
        }

        lock (_gate)
        {
            if (!_clientAccepts)
            {
                return Refuse(LocalText.Of(nameof(Strings.SendNoClient)), "no client that receives files is connected");
            }

            if (_current is { Listener.IsActive: true } || _dropUsesDirect())
            {
                return Refuse(LocalText.Of(nameof(Strings.SendBusy)), "another transfer uses the direct connection");
            }
        }

        if (paths.Count == 0)
        {
            return Refuse(LocalText.Of(nameof(Strings.SendNothing)), "no path");
        }

        var plan = LocalMessages.PlanSend(paths, Kind, out var problem, out var problemPath);
        if (plan is not { } planned)
        {
            return problem switch
            {
                SendPlanProblem.SameName => Refuse(LocalText.Of(nameof(Strings.SendSameName), TransferPaths.ItemName(problemPath ?? string.Empty)), "two items have the same name"),
                SendPlanProblem.BadName => Refuse(LocalText.Of(nameof(Strings.SendBadName), problemPath), "a name is not a file name"),
                SendPlanProblem.Missing => Refuse(LocalText.Of(nameof(Strings.SendMissing), problemPath), "a path does not exist"),
                _ => Refuse(LocalText.Of(nameof(Strings.SendNothing)), "no path"),
            };
        }

        List<(string Path, TransferFile File)> files;
        try
        {
            files = TransferListing.List(planned.Drops);
        }
        catch (TransferListingException ex)
        {
            return Refuse(LocalText.Of(nameof(Strings.SendCannotList), ex.ItemPath, ex.Message), "an item cannot be listed: " + ex.Message);
        }

        var id = Guid.NewGuid().ToString("N");
        var bytes = files.Sum(file => file.File.Size);
        var listener = DirectSendListener.TryStart(_logger, _settings.DirectPortRange, _settings.DirectConnections, _settings.DirectMaxMegabytesPerSecond, id, files);
        if (listener is null)
        {
            return Refuse(LocalText.Of(nameof(Strings.SendNoPort)), "no direct connection can be offered");
        }

        var pending = new Pending(id, listener);
        lock (_gate)
        {
            if (_current is { Listener.IsActive: true })
            {
                listener.Close("another send started meanwhile");
                return Refuse(LocalText.Of(nameof(Strings.SendBusy)), "another send started meanwhile");
            }

            _current = pending;
        }

        listener.Finished += (_, outcome) => OnFinished(pending, outcome);
        var offer = new SendOfferMessage(id, files.Count, bytes, planned.Items[0].Name, planned.Items, listener.Offer);
        _logger.Info(string.Create(CultureInfo.InvariantCulture, $"Send {id}: {planned.Items.Count} item(s), {files.Count} file(s), {bytes} bytes offered to the client."));
        if (!_send(offer))
        {
            listener.Close("the offer could not be sent through the channel");
            return Refuse(LocalText.Of(nameof(Strings.SendChannelFailed)), "the offer could not be sent");
        }

        if (!pending.Answered.Wait(AckWait))
        {
            listener.Close(string.Create(CultureInfo.InvariantCulture, $"the client did not answer the offer within {AckWait.TotalSeconds:0} s"));
            _send(new SendCancelMessage(id, "The client did not answer the offer in time."));
            return Refuse(LocalText.Of(nameof(Strings.SendNoAnswer)), "no answer of the client");
        }

        if (!pending.Accepted)
        {
            return pending.Refusal is { } refusal
                ? new LocalAnswer(false, LocalText.Of(nameof(Strings.SendClientRefused), refusal).ForUser())
                : Refuse(LocalText.Of(nameof(Strings.SendNoAnswer)), "the send ended before the client answered");
        }

        return new LocalAnswer(true);
    }

    /// <summary>The sendAck of the client. Channel thread.</summary>
    public void OnSendAck(SendAckMessage ack)
    {
        var pending = Find(ack.Id);
        if (pending is null)
        {
            _logger.Info($"Send {ack.Id}: answer of the client ignored, no such send.");
            return;
        }

        if (ack.Accepted)
        {
            _logger.Info($"Send {ack.Id}: the client accepted the files; waiting for its connections.");
            pending.Listener.OnAccepted();
        }
        else
        {
            _logger.Info($"Send {ack.Id}: the client refused the files: {ack.Message ?? "(no reason given)"}");
            pending.Refusal = ack.Message;
            pending.Listener.Close("the client refused the files");
        }

        pending.Accepted = ack.Accepted;
        pending.Answered.Set();
    }

    /// <summary>The sendCancel of the client. Channel thread.</summary>
    public void OnSendCancel(SendCancelMessage cancel)
    {
        var pending = Find(cancel.Id);
        if (pending is null)
        {
            _logger.Info($"Send {cancel.Id}: cancel of the client ignored, no such send.");
            return;
        }

        var reason = cancel.NotAvailable == true
            ? $"the client cannot reach the direct connection: {cancel.Reason ?? "(no reason given)"}"
            : $"the client cancelled: {cancel.Reason ?? "(no reason given)"}";
        if (cancel.NotAvailable == true)
        {
            _logger.Warn($"Send {cancel.Id}: {reason}. No other path is used; the user can read the files through the client drives instead.");
        }
        else
        {
            _logger.Info($"Send {cancel.Id}: {reason}.");
        }

        pending.Listener.Close(reason);
    }

    private static bool? Kind(string path) => Directory.Exists(path) ? true : File.Exists(path) ? false : null;

    private LocalAnswer Refuse(LocalText text, string reason)
    {
        _logger.Info($"Send refused: {reason}.");
        return new LocalAnswer(false, text.ForUser());
    }

    private void SetClientAccepts(bool accepts, string reason)
    {
        bool changed;
        lock (_gate)
        {
            changed = _clientAccepts != accepts;
            _clientAccepts = accepts;
        }

        if (changed)
        {
            _logger.Info($"Files of the session can {(accepts && _settings.SendToClient ? string.Empty : "not ")}be sent to the client ({reason}{(accepts && !_settings.SendToClient ? "; the setting sendToClient is never" : string.Empty)}).");
            ClientAcceptsChanged?.Invoke(accepts && _settings.SendToClient);
        }
    }

    private Pending? Find(string id)
    {
        lock (_gate)
        {
            return _current is { } current && current.Id == id ? current : null;
        }
    }

    private void OnFinished(Pending pending, Core.Direct.DirectSendOutcome? outcome)
    {
        _logger.Info($"Send {pending.Id}: over, {(outcome is null ? "no transfer started" : outcome.Status.ToString())}.");
        pending.Answered.Set();
    }

    /// <summary>One send, from its offer to its end.</summary>
    private sealed class Pending(string id, DirectSendListener listener)
    {
        public string Id { get; } = id;

        public DirectSendListener Listener { get; } = listener;

        public ManualResetEventSlim Answered { get; } = new(false);

        public volatile bool Accepted;

        public volatile string? Refusal;
    }
}
