using System.Runtime.InteropServices;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace UltimateRDP.Client.Rdp;

/// <summary>
/// The OLE drop target of one window of a RemoteApp program. It forwards to the session the
/// window belongs to, found when the drag enters, so that a drop on any window of a program
/// reaches the agent of that program. See <see cref="RailDropTargets"/>.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class RailWindowDropTarget : IOleDropTarget
{
    private const int S_OK = 0;

    private readonly Func<IRailSession?> _resolve;
    private IRailSession? _session;
    private IDataObject? _current;

    /// <param name="resolve">Gives the session of the window, or null when none is known.</param>
    internal RailWindowDropTarget(Func<IRailSession?> resolve)
    {
        _resolve = resolve;
    }

    public int OleDragEnter(ComDataObject? dataObject, int keyState, OlePoint pt, ref int effect)
    {
        _current = dataObject is null ? null : new DataObject(dataObject);
        _session = _resolve();
        effect = (int)(_session?.RailDragOver(_current, new Point(pt.X, pt.Y), (DragDropEffects)effect, keyState) ?? DragDropEffects.None);
        return S_OK;
    }

    public int OleDragOver(int keyState, OlePoint pt, ref int effect)
    {
        effect = (int)(_session?.RailDragOver(_current, new Point(pt.X, pt.Y), (DragDropEffects)effect, keyState) ?? DragDropEffects.None);
        return S_OK;
    }

    public int OleDragLeave()
    {
        _current = null;
        var session = _session;
        _session = null;
        session?.RailDragLeave();
        return S_OK;
    }

    public int OleDrop(ComDataObject? dataObject, int keyState, OlePoint pt, ref int effect)
    {
        var data = dataObject is null ? _current : new DataObject(dataObject);
        _current = null;
        var session = _session ?? _resolve();
        _session = null;
        effect = (int)(session?.RailDrop(data, new Point(pt.X, pt.Y), (DragDropEffects)effect, keyState) ?? DragDropEffects.None);
        return S_OK;
    }
}
