using System.Globalization;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Transfer;

/// <summary>Why a <see cref="ReliableInbox"/> gave up.</summary>
public enum InboxFailure
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>More lines waited behind a missing one than the inbox holds.</summary>
    Overflow,

    /// <summary>Missing lines did not come after <see cref="ReliableInbox.MaxRequests"/> requests.</summary>
    Unrecovered,
}

/// <summary>What <see cref="ReliableInbox.Accept"/> or <see cref="ReliableInbox.Check"/> decided.</summary>
/// <param name="Ready">The lines to handle now, in the order of their numbers.</param>
/// <param name="Released">How many of <paramref name="Ready"/> had waited behind a missing line.</param>
/// <param name="Request">The request to send to the client, or null.</param>
/// <param name="Duplicate">True when the line had already arrived: it is ignored.</param>
/// <param name="Failed">True when the inbox gave up just now; see <see cref="ReliableInbox.Failure"/>.</param>
public sealed record InboxStep(IReadOnlyList<Message> Ready, int Released, TransferResendMessage? Request, bool Duplicate, bool Failed)
{
    /// <summary>Nothing to do.</summary>
    public static InboxStep Nothing { get; } = new([], 0, null, false, false);
}

/// <summary>
/// Puts the numbered lines of one transfer back in order on the agent, and asks the client
/// for the missing ones. The channel may lose a whole call of the control without any
/// error on either side; the lines around it arrive intact and in order.
/// </summary>
/// <remarks>
/// <para>
/// Lines are handed on in the order of their <c>seq</c>, from 0. A line whose number is
/// above the next one expected waits here, and the missing range goes to the client at once
/// in a <see cref="TransferResendMessage"/>. While the range is still missing, the request
/// goes again once nothing arrived for <see cref="RequestInterval"/> since the last line
/// and the last request: lines sent before the resend may still be on the way, behind a
/// full window. After <see cref="MaxRequests"/> requests without progress, the inbox gives
/// up with <see cref="InboxFailure.Unrecovered"/>. When the missing lines arrive, the lines
/// that waited are handed on with them, in order. A line that already arrived is a
/// duplicate, from a resend, and is ignored.
/// </para>
/// <para>
/// At most <see cref="MaxHeldCharacters"/> characters of lines wait; beyond that the inbox
/// gives up with <see cref="InboxFailure.Overflow"/>. A client that keeps to its window
/// never reaches it, since nothing behind a missing line is acknowledged.
/// </para>
/// <para>Times are milliseconds of any steady clock. This class is not thread safe.</para>
/// </remarks>
public sealed class ReliableInbox
{
    /// <summary>Most requests for the same missing lines before the transfer fails.</summary>
    public const int MaxRequests = 10;

    /// <summary>
    /// Characters counted for a line that is not a chunk, whose length the message does not
    /// keep. Such lines are a few hundred characters long.
    /// </summary>
    public const int SmallLineCharacters = 256;

    /// <summary>
    /// Most characters that may wait by default: the bound of the queue of an offered
    /// transfer, twice the largest window of the client in base64.
    /// </summary>
    public const long DefaultMaxHeldCharacters = TransferRegistry<object>.MaxQueuedCharacters;

    /// <summary>Silence after which a request goes again while lines are still missing.</summary>
    public static readonly TimeSpan RequestInterval = TimeSpan.FromMilliseconds(500);

    private readonly SortedDictionary<long, (Message Message, long Characters)> _held = [];
    private long _lastArrival;
    private long _lastRequest;
    private long _requestedTo = -1;
    private int _requestsForGap;

    /// <param name="transferId">Identifier of the transfer, for the requests.</param>
    /// <param name="maxHeldCharacters">Most characters that may wait, at least 1.</param>
    public ReliableInbox(string transferId, long maxHeldCharacters = DefaultMaxHeldCharacters)
    {
        ArgumentException.ThrowIfNullOrEmpty(transferId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHeldCharacters, 1);
        TransferId = transferId;
        MaxHeldCharacters = maxHeldCharacters;
    }

    /// <summary>Identifier of the transfer.</summary>
    public string TransferId { get; }

    /// <summary>Most characters that may wait.</summary>
    public long MaxHeldCharacters { get; }

    /// <summary>The number of the next line to hand on; every line below it was handed on.</summary>
    public long Expected { get; private set; }

    /// <summary>Lines waiting behind a missing one.</summary>
    public int HeldLines => _held.Count;

    /// <summary>Characters of the lines waiting.</summary>
    public long HeldCharacters { get; private set; }

    /// <summary>True while lines are missing, which <see cref="Check"/> watches.</summary>
    public bool HasGap => _held.Count > 0 && Failure == InboxFailure.None;

    /// <summary>The first missing range, or null when nothing is missing.</summary>
    public (long From, long To)? Gap => _held.Count > 0 ? (Expected, _held.First().Key - 1) : null;

    /// <summary>Requests sent to the client so far.</summary>
    public int Requests { get; private set; }

    /// <summary>Lines that waited behind a missing one and were handed on once it arrived.</summary>
    public long ReleasedLines { get; private set; }

    /// <summary>Lines that arrived a second time and were ignored.</summary>
    public long DuplicateLines { get; private set; }

    /// <summary>Why the inbox gave up, or <see cref="InboxFailure.None"/>.</summary>
    public InboxFailure Failure { get; private set; }

    /// <summary>The failure in English, for the log.</summary>
    public string? FailureDetail { get; private set; }

    /// <summary>
    /// The number of a line of a transfer: <c>transferBegin</c>, <c>fileBegin</c>,
    /// <c>fileChunk</c> or <c>fileEnd</c> with a <c>seq</c>. Null for any other message,
    /// and for a line of a client that does not number them, which is handled as it arrives.
    /// </summary>
    public static long? SeqOf(Message message) => message switch
    {
        TransferBeginMessage begin => begin.Seq,
        FileBeginMessage file => file.Seq,
        FileChunkMessage chunk => chunk.Seq,
        FileEndMessage end => end.Seq,
        _ => null,
    };

    /// <summary>
    /// A numbered line arrived at <paramref name="nowMilliseconds"/>. Returns the lines to
    /// handle now, in order, and the request to send, if one is due.
    /// </summary>
    /// <exception cref="ArgumentException">The message carries no number.</exception>
    public InboxStep Accept(Message message, long nowMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(message);
        var seq = SeqOf(message) ?? throw new ArgumentException("The line carries no number.", nameof(message));
        if (Failure != InboxFailure.None)
        {
            return InboxStep.Nothing;
        }

        _lastArrival = nowMilliseconds;
        if (seq < Expected || _held.ContainsKey(seq))
        {
            DuplicateLines++;
            return InboxStep.Nothing with { Duplicate = true };
        }

        if (seq > Expected)
        {
            var characters = CharactersOf(message);
            if (HeldCharacters + characters > MaxHeldCharacters)
            {
                Fail(InboxFailure.Overflow, Invariant($"{HeldCharacters + characters} characters of lines waited behind line {Expected}, more than the {MaxHeldCharacters} the agent holds."));
                return InboxStep.Nothing with { Failed = true };
            }

            _held.Add(seq, (message, characters));
            HeldCharacters += characters;
            return InboxStep.Nothing with { Request = Expected > _requestedTo ? Request(nowMilliseconds) : null };
        }

        List<Message> ready = [message];
        Expected++;
        var released = 0;
        while (_held.Remove(Expected, out var next))
        {
            ready.Add(next.Message);
            HeldCharacters -= next.Characters;
            released++;
            Expected++;
        }

        ReleasedLines += released;
        _requestsForGap = 0;

        // A missing range that no request named yet is asked for at once; one already named
        // may still be on its way, and Check asks again after a silence.
        var request = _held.Count > 0 && Expected > _requestedTo ? Request(nowMilliseconds) : null;
        return new InboxStep(ready, released, request, false, false);
    }

    /// <summary>
    /// Called regularly while <see cref="HasGap"/> is true. Asks again for the missing lines
    /// once nothing arrived for <see cref="RequestInterval"/>, and gives up after
    /// <see cref="MaxRequests"/> requests for the same lines.
    /// </summary>
    public InboxStep Check(long nowMilliseconds)
    {
        if (!HasGap || nowMilliseconds - Math.Max(_lastArrival, _lastRequest) < RequestInterval.TotalMilliseconds)
        {
            return InboxStep.Nothing;
        }

        if (_requestsForGap >= MaxRequests)
        {
            var (from, to) = Gap!.Value;
            Fail(InboxFailure.Unrecovered, Invariant($"lines {from} to {to} of the transfer never arrived after {MaxRequests} requests to the client."));
            return InboxStep.Nothing with { Failed = true };
        }

        return InboxStep.Nothing with { Request = Request(nowMilliseconds) };
    }

    private TransferResendMessage Request(long nowMilliseconds)
    {
        var (from, to) = Gap!.Value;
        _requestedTo = Math.Max(_requestedTo, to);
        _requestsForGap++;
        Requests++;
        _lastRequest = nowMilliseconds;
        return new TransferResendMessage(TransferId, from, to);
    }

    private void Fail(InboxFailure failure, string detail)
    {
        Failure = failure;
        FailureDetail = detail;
        _held.Clear();
        HeldCharacters = 0;
    }

    private static long CharactersOf(Message message) => message is FileChunkMessage chunk ? chunk.Data.Length + SmallLineCharacters : SmallLineCharacters;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
