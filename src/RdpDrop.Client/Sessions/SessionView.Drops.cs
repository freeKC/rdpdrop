using System.Globalization;
using RdpDrop.Client.Channel;
using RdpDrop.Client.Logging;
using RdpDrop.Client.Rdp;
using RdpDrop.Core.Paths;
using RdpDrop.Core.Protocol;

namespace RdpDrop.Client.Sessions;

/// <summary>
/// Drop handling of the session: answers drags over the control, sends drop messages to
/// the agent and shows their results.
/// </summary>
/// <remarks>
/// The control, the OLE targets on its child windows and the host window (through
/// <see cref="ProcessDragOver"/> and the other Process methods) all end up in the same
/// handlers, so every path gives the same answer.
/// </remarks>
internal sealed partial class SessionView
{
    private string? _pendingDropId;
    private string? _dropTextBeforeDrag;

    /// <summary>
    /// Answers a drag over the host window at <paramref name="screenPoint"/>. The control
    /// refuses points outside the session area.
    /// </summary>
    public DragDropEffects ProcessDragOver(IDataObject? data, Point screenPoint, DragDropEffects allowed) =>
        _rdp.ProcessDragOver(data, screenPoint, allowed);

    /// <summary>Handles a drop on the host window at <paramref name="screenPoint"/>.</summary>
    public DragDropEffects ProcessDrop(IDataObject? data, Point screenPoint, DragDropEffects allowed) =>
        _rdp.ProcessDrop(data, screenPoint, allowed);

    /// <summary>Handles the end of a drag over the host window that did not drop.</summary>
    public void ProcessDragLeave() => _rdp.ProcessDragLeave();

    /// <summary>
    /// Why a drop cannot be sent now, or null when it can.
    /// </summary>
    private string? DropRefusalReason()
    {
        if (State != SessionState.Connected)
        {
            return "Drop refused: not connected.";
        }

        return _agent.State switch
        {
            AgentState.Ready => null,
            AgentState.Waiting => "Drop refused: the RdpDrop agent has not answered yet.",
            _ => "Drop refused: the RdpDrop agent is not running in the remote session.",
        };
    }

    private void OnFileDragOver(object? sender, FileDragEventArgs e)
    {
        _dropTextBeforeDrag ??= DropText;
        var reason = DropRefusalReason();
        if (reason is null)
        {
            e.Effect = DragDropEffects.Copy;
            ShowDropStatus(_dropTextBeforeDrag);
        }
        else
        {
            e.Effect = DragDropEffects.None;
            ShowDropStatus(reason);
        }
    }

    private void OnFileDragLeft()
    {
        if (_dropTextBeforeDrag is not null)
        {
            ShowDropStatus(_dropTextBeforeDrag);
            _dropTextBeforeDrag = null;
        }
    }

    private void OnFileDropped(object? sender, FileDragEventArgs e)
    {
        _dropTextBeforeDrag = null;
        var reason = DropRefusalReason();
        if (reason is not null)
        {
            ClientLog.Info(Invariant($"Drop of {e.Files.Count} item(s) refused: {reason}"));
            ShowDropStatus(reason);
            return;
        }

        var items = new List<DropItem>(e.Files.Count);
        foreach (var path in e.Files)
        {
            if (!ClientPathMapper.TryMapToTsClient(path, out _, out var pathProblem))
            {
                ClientLog.Info($"Drop refused: {pathProblem}");
                ShowDropStatus($"Drop refused: {pathProblem}");
                return;
            }

            items.Add(new DropItem(path, Directory.Exists(path)));
        }

        var viewSize = _rdp.ViewSize;
        var desktopSize = _display.RemoteDesktopSize;
        var remote = DropCoordinateMapper.ToRemote(e.ClientPoint, viewSize, desktopSize);
        var drop = new DropMessage(Guid.NewGuid().ToString("N"), remote.X, remote.Y, items);
        ClientLog.Info(Invariant(
            $"Drop {drop.Id} on {Host}: {items.Count} item(s) at view ({e.ClientPoint.X}, {e.ClientPoint.Y}) of {viewSize.Width}x{viewSize.Height}{(IsFullScreen ? " full screen" : string.Empty)}, remote ({remote.X}, {remote.Y}) of {desktopSize.Width}x{desktopSize.Height}, model {_agent.Model}: {string.Join("; ", e.Files)}"));

        if (!_agent.SendDrop(drop))
        {
            ShowDropStatus("Drop failed: the message could not be sent to the agent.");
            return;
        }

        _pendingDropId = drop.Id;
        e.Effect = DragDropEffects.Copy;
        ShowDropStatus(Invariant($"Sending {items.Count} item(s)..."));
    }

    private void OnDropResult(DropResultMessage result)
    {
        if (_pendingDropId is not null && result.Id != _pendingDropId)
        {
            ClientLog.Info($"Result {result.Id} belongs to an earlier drop; the status bar keeps the latest one.");
            return;
        }

        _pendingDropId = null;
        ShowDropStatus(DescribeResult(result));
    }

    private static string DescribeResult(DropResultMessage result) => result.Status switch
    {
        DropStatus.Ok => !string.IsNullOrWhiteSpace(result.Message)
            ? result.Message
            : $"Copied to {result.TargetFolder}",
        DropStatus.Cancelled => !string.IsNullOrWhiteSpace(result.Message)
            ? $"Cancelled: {result.Message}"
            : "Copy cancelled.",
        _ => $"Drop failed: {(string.IsNullOrWhiteSpace(result.Message) ? "no detail from the agent." : result.Message)}",
    };
}
