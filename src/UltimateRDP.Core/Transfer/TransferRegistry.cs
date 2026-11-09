using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Transfer;

/// <summary>What the agent does with a message of a transfer, as <see cref="TransferRegistry{TTarget}"/> decides.</summary>
public enum TransferRoute
{
    /// <summary>The transfer is running: hand the message to its receiver.</summary>
    Deliver,

    /// <summary>The transfer was offered and its receiver does not exist yet: the message waits.</summary>
    Queued,

    /// <summary>The transfer is over: the message is dropped without an answer.</summary>
    Late,

    /// <summary>The agent never offered this transfer: answer once with an error result.</summary>
    Unknown,

    /// <summary>Too much arrived before the receiver existed: the transfer is over, answer with an error result.</summary>
    Overflow,
}

/// <summary>
/// The transfers of the agent by id, from the moment a <c>dragAck</c> offers one: offered,
/// running (attached to its receiver) or ended. A message of an offered transfer that
/// arrives before its receiver exists waits in a queue, and <see cref="Attach"/> returns
/// the waiting messages in their order, so that no timing between the deferred drop and
/// the first messages of the transfer can lose one.
/// </summary>
/// <remarks>
/// <para>
/// The agent calls <see cref="Offer"/> before the <c>dragAck</c> that carries the id goes
/// out, <see cref="Attach"/> when the deferred drop creates the receiver,
/// <see cref="Withdraw"/> when the drag ends without one, and <see cref="End"/> once the
/// transfer is over. Every message of a transfer goes through <see cref="Route"/>.
/// </para>
/// <para>This class is not thread safe: the caller holds its own lock.</para>
/// </remarks>
/// <typeparam name="TTarget">The receiver of a running transfer.</typeparam>
public sealed class TransferRegistry<TTarget>
    where TTarget : class
{
    /// <summary>How many ended transfers are remembered, so that their late messages are dropped quietly.</summary>
    public const int RememberedTransfers = 16;

    /// <summary>
    /// Most characters of chunk data that may wait for a receiver: twice the window of the
    /// client in base64. A client that keeps to its window never reaches it.
    /// </summary>
    public const long MaxQueuedCharacters = 2 * ((TransferSender.WindowBytes + TransferSender.MaxChunkSize + 2) / 3 * 4);

    private readonly Dictionary<string, Entry> _active = new(StringComparer.Ordinal);
    private readonly Queue<string> _ended = new();
    private readonly Dictionary<string, int> _late = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a transfer offered to the client. From now on its messages are accepted,
    /// whatever the timing of the deferred drop.
    /// </summary>
    /// <exception cref="InvalidOperationException">The id is already known.</exception>
    public void Offer(string transferId)
    {
        ArgumentException.ThrowIfNullOrEmpty(transferId);
        if (_active.ContainsKey(transferId) || _ended.Contains(transferId))
        {
            throw new InvalidOperationException($"Transfer {transferId} is already known.");
        }

        _active[transferId] = new Entry();
    }

    /// <summary>True while the transfer is offered and has no receiver yet.</summary>
    public bool IsOffered(string transferId) => _active.TryGetValue(transferId, out var entry) && entry.Target is null;

    /// <summary>The receiver of a running transfer, or null.</summary>
    public TTarget? Find(string transferId) => _active.TryGetValue(transferId, out var entry) ? entry.Target : null;

    /// <summary>Number of messages of an ended transfer that <see cref="Route"/> found late so far.</summary>
    public int LateCount(string transferId) => _late.GetValueOrDefault(transferId);

    /// <summary>Number of messages waiting for the receiver of an offered transfer.</summary>
    public int QueuedCount(string transferId) => _active.TryGetValue(transferId, out var entry) ? entry.Queue.Count : 0;

    /// <summary>
    /// Gives an offered transfer its receiver. Returns the messages that arrived before, in
    /// their order; the caller hands them to the receiver before any later message.
    /// </summary>
    /// <exception cref="InvalidOperationException">The transfer is not offered, or already has a receiver.</exception>
    public IReadOnlyList<Message> Attach(string transferId, TTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_active.TryGetValue(transferId, out var entry) || entry.Target is not null)
        {
            throw new InvalidOperationException($"Transfer {transferId} is not waiting for a receiver.");
        }

        entry.Target = target;
        var queued = entry.Queue.ToArray();
        entry.Queue.Clear();
        entry.QueuedCharacters = 0;
        return queued;
    }

    /// <summary>
    /// The drag of an offered transfer ended without a deferred drop. Returns true when
    /// messages of the transfer were waiting: the client started it and expects a result.
    /// Later messages are answered as for an unknown transfer. Does nothing for a transfer
    /// that has a receiver.
    /// </summary>
    public bool Withdraw(string transferId)
    {
        if (!_active.TryGetValue(transferId, out var entry) || entry.Target is not null)
        {
            return false;
        }

        _active.Remove(transferId);
        return entry.Queue.Count > 0;
    }

    /// <summary>The transfer is over: its late messages are dropped without an answer.</summary>
    public void End(string transferId)
    {
        ArgumentException.ThrowIfNullOrEmpty(transferId);
        _active.Remove(transferId);
        Remember(transferId);
    }

    /// <summary>
    /// Decides what happens to a message of <paramref name="transferId"/>. For
    /// <see cref="TransferRoute.Deliver"/>, <paramref name="target"/> is the receiver. An
    /// unknown transfer is remembered, so that it gets one answer only. An overflow ends
    /// the transfer.
    /// </summary>
    public TransferRoute Route(Message message, string transferId, out TTarget? target)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(transferId);
        target = null;
        if (_active.TryGetValue(transferId, out var entry))
        {
            if (entry.Target is not null)
            {
                target = entry.Target;
                return TransferRoute.Deliver;
            }

            entry.QueuedCharacters += message is FileChunkMessage chunk ? chunk.Data.Length : 1;
            if (entry.QueuedCharacters > MaxQueuedCharacters)
            {
                End(transferId);
                return TransferRoute.Overflow;
            }

            entry.Queue.Add(message);
            return TransferRoute.Queued;
        }

        if (_ended.Contains(transferId))
        {
            _late[transferId] = _late.GetValueOrDefault(transferId) + 1;
            return TransferRoute.Late;
        }

        Remember(transferId);
        return TransferRoute.Unknown;
    }

    private void Remember(string transferId)
    {
        if (_ended.Contains(transferId))
        {
            return;
        }

        _ended.Enqueue(transferId);
        while (_ended.Count > RememberedTransfers)
        {
            _late.Remove(_ended.Dequeue());
        }
    }

    private sealed class Entry
    {
        public List<Message> Queue { get; } = [];

        public long QueuedCharacters { get; set; }

        public TTarget? Target { get; set; }
    }
}
