using System.Runtime.InteropServices;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace RdpDrop.Client.Rdp;

/// <summary>
/// OLE drop target registered on a child window of the ActiveX control. It forwards to
/// <see cref="RdpControl"/>, so a drop on any window of the session gets the same answer.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ChildWindowDropTarget : IOleDropTarget
{
    private const int S_OK = 0;

    private readonly RdpControl _control;
    private IDataObject? _current;

    internal ChildWindowDropTarget(RdpControl control)
    {
        _control = control;
    }

    public int OleDragEnter(ComDataObject? dataObject, int keyState, OlePoint pt, ref int effect)
    {
        _current = dataObject is null ? null : new DataObject(dataObject);
        effect = (int)_control.ProcessDragOver(_current, new Point(pt.X, pt.Y), (DragDropEffects)effect, keyState);
        return S_OK;
    }

    public int OleDragOver(int keyState, OlePoint pt, ref int effect)
    {
        effect = (int)_control.ProcessDragOver(_current, new Point(pt.X, pt.Y), (DragDropEffects)effect, keyState);
        return S_OK;
    }

    public int OleDragLeave()
    {
        _current = null;
        _control.ProcessDragLeave();
        return S_OK;
    }

    public int OleDrop(ComDataObject? dataObject, int keyState, OlePoint pt, ref int effect)
    {
        var data = dataObject is null ? _current : new DataObject(dataObject);
        _current = null;
        effect = (int)_control.ProcessDrop(data, new Point(pt.X, pt.Y), (DragDropEffects)effect, keyState);
        return S_OK;
    }
}
