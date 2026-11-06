using AxMSTSCLib;

namespace RdpDrop.Client.Rdp;

/// <summary>
/// The Remote Desktop ActiveX control, with file drops turned into .NET events.
/// </summary>
/// <remarks>
/// AxHost throws <see cref="NotSupportedException"/> when code subscribes to its
/// DragEnter, DragOver, DragDrop or DragLeave events, so the virtual On* methods are
/// overridden instead. The form and the OLE targets registered on the child windows of
/// the control forward to the same Process* methods, so every path gives the same answer.
/// </remarks>
internal sealed class RdpControl : AxMsRdpClient9NotSafeForScripting
{
    /// <summary>Raised while files are dragged over the control. The handler sets the effect.</summary>
    public event EventHandler<FileDragEventArgs>? FileDragOver;

    /// <summary>Raised when files are dropped on the control. The handler sets the effect.</summary>
    public event EventHandler<FileDragEventArgs>? FileDropped;

    /// <summary>Raised when a drag leaves the control or is cancelled.</summary>
    public event EventHandler? FileDragLeft;

    /// <summary>
    /// Screen area covered by the session while the control runs full screen in its own
    /// window, or null in a window. Drop points are then taken relative to this area.
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Rectangle? FullScreenBounds { get; set; }

    /// <summary>Size of the area that shows the session: the full screen area or the client area.</summary>
    public Size ViewSize => FullScreenBounds?.Size ?? ClientSize;

    /// <summary>
    /// Answers a drag enter or drag over at <paramref name="screenPoint"/>.
    /// </summary>
    public DragDropEffects ProcessDragOver(IDataObject? data, Point screenPoint, DragDropEffects allowed, int oleKeyState = 0) =>
        Raise(FileDragOver, data, screenPoint, allowed, oleKeyState);

    /// <summary>
    /// Handles a drop at <paramref name="screenPoint"/> and returns the effect to report.
    /// </summary>
    public DragDropEffects ProcessDrop(IDataObject? data, Point screenPoint, DragDropEffects allowed, int oleKeyState = 0) =>
        Raise(FileDropped, data, screenPoint, allowed, oleKeyState);

    /// <summary>
    /// Handles the end of a drag that did not drop here.
    /// </summary>
    public void ProcessDragLeave() => FileDragLeft?.Invoke(this, EventArgs.Empty);

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        drgevent.Effect = ProcessDragOver(drgevent.Data, new Point(drgevent.X, drgevent.Y), drgevent.AllowedEffect, drgevent.KeyState);
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        drgevent.Effect = ProcessDragOver(drgevent.Data, new Point(drgevent.X, drgevent.Y), drgevent.AllowedEffect, drgevent.KeyState);
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        drgevent.Effect = ProcessDrop(drgevent.Data, new Point(drgevent.X, drgevent.Y), drgevent.AllowedEffect, drgevent.KeyState);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        ProcessDragLeave();
    }

    private DragDropEffects Raise(EventHandler<FileDragEventArgs>? handler, IDataObject? data, Point screenPoint, DragDropEffects allowed, int oleKeyState)
    {
        if (handler is null || (allowed & DragDropEffects.Copy) == 0 || !IsHandleCreated)
        {
            return DragDropEffects.None;
        }

        var files = FileDropData.GetFiles(data);
        if (files is null)
        {
            return DragDropEffects.None;
        }

        var clientPoint = FullScreenBounds is { } bounds
            ? new Point(screenPoint.X - bounds.X, screenPoint.Y - bounds.Y)
            : PointToClient(screenPoint);
        if (!new Rectangle(Point.Empty, ViewSize).Contains(clientPoint))
        {
            return DragDropEffects.None;
        }

        var args = new FileDragEventArgs(files, clientPoint, FileDragEventArgs.FromOleKeyState(oleKeyState));
        handler(this, args);
        return args.Effect == DragDropEffects.Copy ? DragDropEffects.Copy : DragDropEffects.None;
    }
}
