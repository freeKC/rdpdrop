using RdpDrop.Agent.Localization;
using RdpDrop.Agent.Resources;

namespace RdpDrop.Agent.Drops;

/// <summary>
/// The decisions of the drag source. OLE calls QueryContinueDrag on the drag thread; the
/// answer depends only on what the client sent, never on the state of the buttons, so a
/// lost or stray button event cannot drop or cancel by itself. Flags are set from the
/// channel thread and read on the drag thread.
/// </summary>
internal sealed class DropSource
{
    private volatile bool _dropRequested;
    private volatile bool _released;
    private volatile bool _cancelRequested;
    private volatile LocalText? _cancelReason;
    private int _feedbackCount;

    /// <summary>True once the client sent the drop.</summary>
    public bool DropRequested => _dropRequested;

    /// <summary>True once the simulated button was released at the drop point.</summary>
    public bool Released => _released;

    /// <summary>True once the drag must end without a drop.</summary>
    public bool CancelRequested => _cancelRequested;

    /// <summary>Why the drag was cancelled.</summary>
    public LocalText? CancelReason => _cancelReason;

    /// <summary>Number of GiveFeedback calls: each one follows a DragEnter or DragOver on the target.</summary>
    public int FeedbackCount => Volatile.Read(ref _feedbackCount);

    public void RequestDrop() => _dropRequested = true;

    public void MarkReleased() => _released = true;

    /// <summary>Asks for a cancel. The first reason is kept. Returns false when a cancel was already asked.</summary>
    public bool RequestCancel(LocalText reason)
    {
        if (_cancelRequested)
        {
            return false;
        }

        _cancelReason = reason;
        _cancelRequested = true;
        return true;
    }

    public void OnFeedback() => Interlocked.Increment(ref _feedbackCount);

    /// <summary>
    /// Answer to QueryContinueDrag: cancel when asked or when Escape reached the drag
    /// thread, drop once the client dropped and the button was released, continue
    /// otherwise.
    /// </summary>
    public DragAction Decide(bool escapePressed)
    {
        if (_cancelRequested)
        {
            return DragAction.Cancel;
        }

        if (escapePressed)
        {
            RequestCancel(LocalText.Of(nameof(Strings.CancelEscape)));

            return DragAction.Cancel;
        }

        return _dropRequested && _released ? DragAction.Drop : DragAction.Continue;
    }
}
