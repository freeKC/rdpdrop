using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Transfer;

/// <summary>Where a <see cref="DropDecision"/> stands.</summary>
public enum DropDecisionState
{
    /// <summary>The drop is held: the dragAck of its drag has not arrived yet.</summary>
    Waiting,

    /// <summary>The agent accepted the drag and offered a transfer: the drop goes out deferred, and the files follow through the channel.</summary>
    Transfer,

    /// <summary>The agent accepted the drag without a transfer: the drop goes out as it is, and the files are read through drive redirection.</summary>
    Plain,

    /// <summary>The agent refused the drag: the drop is not sent.</summary>
    Refused,

    /// <summary>
    /// No final dragAck came within <see cref="DropDecision.MaxWait"/>: the drop goes out
    /// without a transfer. <see cref="DropDecision.ProvisionalAnswer"/> tells whether the
    /// agent answered at all.
    /// </summary>
    TimedOut,

    /// <summary>The client gave the drop up before the agent answered: cancelled by the user, or the session ended.</summary>
    Abandoned,
}

/// <summary>
/// Decides what the client sends for a local drop that happened while the dragAck of its
/// drag was still on its way. Sending the drop at once would lose the transfer that the
/// dragAck may offer: the drop is held until the dragAck arrives, for at most
/// <see cref="MaxWait"/>, then goes out deferred when a transfer was offered, as it is
/// otherwise, or not at all when the drag was refused.
/// </summary>
/// <remarks>
/// A provisional dragAck (<see cref="DragAckMessage.Provisional"/>) does not decide: the
/// agent sent it before it decided the transfer, and a final dragAck follows. The drop
/// stays held for that one, within the same <see cref="MaxWait"/>; a drop that happens
/// after a provisional answer is held as well (<see cref="MustHold"/>). Times are
/// milliseconds of any steady clock. The first final answer decides; later ones are
/// ignored. This class is not thread safe.
/// </remarks>
public sealed class DropDecision
{
    /// <summary>Longest time a drop is held for the dragAck of its drag.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(1500);

    /// <param name="dragId">The drag whose drop is held.</param>
    /// <param name="heldMilliseconds">When the local drop happened.</param>
    /// <param name="provisionalAnswer">True when a provisional dragAck arrived before the drop.</param>
    public DropDecision(string dragId, long heldMilliseconds, bool provisionalAnswer = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(dragId);
        DragId = dragId;
        HeldAt = heldMilliseconds;
        ProvisionalAnswer = provisionalAnswer;
    }

    /// <summary>
    /// True when a local drop must be held: no dragAck of its drag arrived yet
    /// (<paramref name="lastAck"/> is null), or only a provisional one.
    /// </summary>
    public static bool MustHold(DragAckMessage? lastAck) => lastAck is null || !IsFinal(lastAck);

    /// <summary>
    /// True for a dragAck that decides the drop: a refusal, an accept that offers a
    /// transfer, or an accept that is not provisional.
    /// </summary>
    public static bool IsFinal(DragAckMessage ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        return !ack.Accepted || ack.Transfer == true || ack.Provisional != true;
    }

    /// <summary>True once a provisional dragAck of the drag arrived, before the drop or during the wait.</summary>
    public bool ProvisionalAnswer { get; private set; }

    /// <summary>The drag whose drop is held.</summary>
    public string DragId { get; }

    /// <summary>When the local drop happened.</summary>
    public long HeldAt { get; }

    /// <summary>When the decision was taken, or null while it waits.</summary>
    public long? DecidedAt { get; private set; }

    /// <summary>Where the decision stands.</summary>
    public DropDecisionState State { get; private set; } = DropDecisionState.Waiting;

    /// <summary>The transfer offered by the dragAck, when <see cref="State"/> is <see cref="DropDecisionState.Transfer"/>.</summary>
    public string? TransferId { get; private set; }

    /// <summary>
    /// The direct connection offered with the transfer since 0.6.0, when the dragAck carried
    /// a well formed one; null otherwise.
    /// </summary>
    public DirectTransferOffer? Direct { get; private set; }

    /// <summary>True when the decision is taken and the drop message goes out.</summary>
    public bool SendsDrop => State is DropDecisionState.Transfer or DropDecisionState.Plain or DropDecisionState.TimedOut;

    /// <summary>The value of <see cref="DropMessage.Deferred"/> for the drop that goes out.</summary>
    public bool? Deferred => State == DropDecisionState.Transfer ? true : null;

    /// <summary>How long the drop was held, until the decision or until now when it still waits.</summary>
    public long WaitedMilliseconds(long nowMilliseconds) => Math.Max(0, (DecidedAt ?? nowMilliseconds) - HeldAt);

    /// <summary>Time left before the wait ends at <paramref name="nowMilliseconds"/>; zero once it is over.</summary>
    public long RemainingMilliseconds(long nowMilliseconds) =>
        Math.Max(0, HeldAt + (long)MaxWait.TotalMilliseconds - nowMilliseconds);

    /// <summary>
    /// The dragAck <paramref name="ack"/> arrived at <paramref name="nowMilliseconds"/>.
    /// Returns true when it took the decision; false when the decision was already taken,
    /// the answer belongs to another drag, or it is provisional and the wait goes on.
    /// </summary>
    public bool OnAck(DragAckMessage ack, long nowMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(ack);
        if (State != DropDecisionState.Waiting || ack.Id != DragId)
        {
            return false;
        }

        if (!IsFinal(ack))
        {
            ProvisionalAnswer = true;
            return false;
        }

        if (!ack.Accepted)
        {
            Decide(DropDecisionState.Refused, nowMilliseconds);
        }
        else if (ack.Transfer == true && !string.IsNullOrEmpty(ack.TransferId))
        {
            TransferId = ack.TransferId;
            Direct = ack.Direct is { } direct && direct.IsWellFormed() ? direct : null;
            Decide(DropDecisionState.Transfer, nowMilliseconds);
        }
        else
        {
            Decide(DropDecisionState.Plain, nowMilliseconds);
        }

        return true;
    }

    /// <summary>
    /// Ends the wait when <see cref="MaxWait"/> has passed at <paramref name="nowMilliseconds"/>.
    /// Returns true when it took the decision.
    /// </summary>
    public bool Expire(long nowMilliseconds)
    {
        if (State != DropDecisionState.Waiting || RemainingMilliseconds(nowMilliseconds) > 0)
        {
            return false;
        }

        Decide(DropDecisionState.TimedOut, nowMilliseconds);
        return true;
    }

    /// <summary>
    /// The client gives the drop up at <paramref name="nowMilliseconds"/>. Returns true when
    /// the drop was still waiting.
    /// </summary>
    public bool Abandon(long nowMilliseconds)
    {
        if (State != DropDecisionState.Waiting)
        {
            return false;
        }

        Decide(DropDecisionState.Abandoned, nowMilliseconds);
        return true;
    }

    /// <summary>The outcome in English, for the log.</summary>
    public string Describe() => State switch
    {
        DropDecisionState.Transfer when Direct is not null => $"the agent offered transfer {TransferId} with a direct connection on port {Direct.Port}, so the drop goes out deferred and the files follow through the direct connection, or the channel when it cannot be used",
        DropDecisionState.Transfer => $"the agent offered transfer {TransferId}, so the drop goes out deferred and the files follow through the channel",
        DropDecisionState.Plain => "the agent accepted the drag without a transfer, so the drop goes out and the files are read through drive redirection",
        DropDecisionState.Refused => "the agent refused the drag, so the drop is not sent",
        DropDecisionState.TimedOut when ProvisionalAnswer => $"only a provisional dragAck within {(long)MaxWait.TotalMilliseconds} ms, so the drop goes out without a transfer and the files are read through drive redirection",
        DropDecisionState.TimedOut => $"no dragAck within {(long)MaxWait.TotalMilliseconds} ms, so the drop goes out without a transfer and the files are read through drive redirection",
        DropDecisionState.Abandoned => "the drop was given up before the agent answered",
        _ => "the answer of the agent is still awaited",
    };

    private void Decide(DropDecisionState state, long nowMilliseconds)
    {
        State = state;
        DecidedAt = nowMilliseconds;
    }
}
