using AxMSTSCLib;
using UltimateRDP.Core.Display;

namespace UltimateRDP.Client.Rdp;

/// <summary>
/// The Remote Desktop ActiveX control, with file drops turned into .NET events.
/// </summary>
/// <remarks>
/// AxHost throws <see cref="NotSupportedException"/> when code subscribes to its
/// DragEnter, DragOver, DragDrop or DragLeave events, so the virtual On* methods are
/// overridden instead. The form and the OLE targets registered on the child windows of
/// the control forward to the same Process* methods, so every path gives the same answer.
/// In RemoteApp mode the control shows nothing: only the windows of the program take drops,
/// through <see cref="ProcessRemoteWindowDragOver"/> and <see cref="ProcessRemoteWindowDrop"/>,
/// and the points are taken relative to the virtual screen, which the desktop of the
/// session copies.
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

    /// <summary>
    /// True while the control runs a RemoteApp program: drops only come from the windows of
    /// the program, and points are relative to the virtual screen.
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool RemoteAppMode { get; set; }

    /// <summary>
    /// Size of the area that shows the session: the virtual screen in RemoteApp mode, the
    /// full screen area or the client area otherwise.
    /// </summary>
    public Size ViewSize => RemoteAppMode ? SystemInformation.VirtualScreen.Size : FullScreenBounds?.Size ?? ClientSize;

    /// <summary>
    /// Answers a drag enter or drag over at <paramref name="screenPoint"/>.
    /// </summary>
    public DragDropEffects ProcessDragOver(IDataObject? data, Point screenPoint, DragDropEffects allowed, int oleKeyState = 0) =>
        Raise(FileDragOver, data, screenPoint, allowed, oleKeyState, fromRemoteWindow: false, out _);

    /// <summary>
    /// Handles a drop at <paramref name="screenPoint"/> and returns the effect to report.
    /// </summary>
    public DragDropEffects ProcessDrop(IDataObject? data, Point screenPoint, DragDropEffects allowed, int oleKeyState = 0) =>
        Drop(data, screenPoint, allowed, oleKeyState, fromRemoteWindow: false);

    /// <summary>Answers a drag over a window of the RemoteApp program at <paramref name="screenPoint"/>.</summary>
    public DragDropEffects ProcessRemoteWindowDragOver(IDataObject? data, Point screenPoint, DragDropEffects allowed, int oleKeyState) =>
        Raise(FileDragOver, data, screenPoint, allowed, oleKeyState, fromRemoteWindow: true, out _);

    /// <summary>Handles a drop on a window of the RemoteApp program at <paramref name="screenPoint"/>.</summary>
    public DragDropEffects ProcessRemoteWindowDrop(IDataObject? data, Point screenPoint, DragDropEffects allowed, int oleKeyState) =>
        Drop(data, screenPoint, allowed, oleKeyState, fromRemoteWindow: true);

    private DragDropEffects Drop(IDataObject? data, Point screenPoint, DragDropEffects allowed, int oleKeyState, bool fromRemoteWindow)
    {
        var effect = Raise(FileDropped, data, screenPoint, allowed, oleKeyState, fromRemoteWindow, out var raised);
        if (!raised)
        {
            // The drop landed outside the session, for example on the status bar of the
            // host window: the drag over the session ends here, as after a leave.
            ProcessDragLeave();
        }

        return effect;
    }

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

    private DragDropEffects Raise(EventHandler<FileDragEventArgs>? handler, IDataObject? data, Point screenPoint, DragDropEffects allowed, int oleKeyState, bool fromRemoteWindow, out bool raised)
    {
        raised = false;
        if (handler is null || (allowed & DragDropEffects.Copy) == 0 || !IsHandleCreated || RemoteAppMode != fromRemoteWindow)
        {
            // In RemoteApp mode a drop on the window of the client is not a drop on the program.
            return DragDropEffects.None;
        }

        var files = FileDropData.GetFiles(data);
        if (files is null)
        {
            return DragDropEffects.None;
        }

        Point clientPoint;
        if (RemoteAppMode)
        {
            var screen = SystemInformation.VirtualScreen;
            var (x, y) = ScreenMapping.ToSessionPoint(screenPoint.X, screenPoint.Y, new ScreenRect(screen.X, screen.Y, screen.Width, screen.Height));
            clientPoint = new Point(x, y);
        }
        else
        {
            clientPoint = FullScreenBounds is { } bounds
                ? new Point(screenPoint.X - bounds.X, screenPoint.Y - bounds.Y)
                : PointToClient(screenPoint);
        }

        if (!new Rectangle(Point.Empty, ViewSize).Contains(clientPoint))
        {
            return DragDropEffects.None;
        }

        var args = new FileDragEventArgs(files, clientPoint, FileDragEventArgs.FromOleKeyState(oleKeyState)) { ScreenPoint = screenPoint };
        raised = true;
        handler(this, args);
        return args.Effect == DragDropEffects.Copy ? DragDropEffects.Copy : DragDropEffects.None;
    }
}
