namespace UltimateRDP.Core.Transfer;

/// <summary>Which acknowledgement rule a transfer broke, as <see cref="TransferAckWatch"/> tells.</summary>
public enum TransferAckTimeout
{
    /// <summary>No rule is broken.</summary>
    None,

    /// <summary>The first chunk went out <see cref="TransferAckWatch.FirstAckTimeout"/> ago and nothing was acknowledged since.</summary>
    FirstData,

    /// <summary>The client waits for acknowledgements and none came for <see cref="TransferAckWatch.AckTimeout"/>.</summary>
    Silent,
}

/// <summary>
/// Watches the acknowledgements of a transfer on the client. The agent acknowledges the
/// first chunk of every file at once, so a transfer whose first data does not get through
/// fails after <see cref="FirstAckTimeout"/> instead of a full minute. After that, the
/// client gives up when it waits for acknowledgements (the window is full, or every file
/// went out) and none came for <see cref="AckTimeout"/>.
/// </summary>
/// <remarks>Times are milliseconds of any steady clock. This class is not thread safe.</remarks>
public sealed class TransferAckWatch
{
    /// <summary>Longest wait for the first acknowledgement after the first chunk went out.</summary>
    public static readonly TimeSpan FirstAckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Longest wait without any acknowledgement while the client waits for one.</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(60);

    private long _lastAck;
    private long? _firstChunkSent;

    /// <param name="startMilliseconds">When the transfer began; the silence is counted from there until the first acknowledgement.</param>
    public TransferAckWatch(long startMilliseconds)
    {
        _lastAck = startMilliseconds;
    }

    /// <summary>True once the agent acknowledged anything.</summary>
    public bool HasAck { get; private set; }

    /// <summary>When the first chunk went out, or null before.</summary>
    public long? FirstChunkSent => _firstChunkSent;

    /// <summary>A chunk went out at <paramref name="nowMilliseconds"/>; only the first one counts.</summary>
    public void OnChunkSent(long nowMilliseconds) => _firstChunkSent ??= nowMilliseconds;

    /// <summary>The agent acknowledged bytes at <paramref name="nowMilliseconds"/>.</summary>
    public void OnAck(long nowMilliseconds)
    {
        HasAck = true;
        _lastAck = nowMilliseconds;
    }

    /// <summary>
    /// The rule broken at <paramref name="nowMilliseconds"/>, if any.
    /// <paramref name="waitingForAck"/> is true while the client cannot send because the
    /// window is full, or once every file went out.
    /// </summary>
    public TransferAckTimeout Check(long nowMilliseconds, bool waitingForAck)
    {
        if (!HasAck && _firstChunkSent is { } first && nowMilliseconds - first > FirstAckTimeout.TotalMilliseconds)
        {
            return TransferAckTimeout.FirstData;
        }

        if (waitingForAck && nowMilliseconds - _lastAck > AckTimeout.TotalMilliseconds)
        {
            return TransferAckTimeout.Silent;
        }

        return TransferAckTimeout.None;
    }
}
