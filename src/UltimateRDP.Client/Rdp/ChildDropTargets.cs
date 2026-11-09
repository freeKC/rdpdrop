using System.Globalization;
using UltimateRDP.Client.Logging;

namespace UltimateRDP.Client.Rdp;

/// <summary>
/// Registers a <see cref="ChildWindowDropTarget"/> on every child window of the ActiveX
/// control, revoking any registration that was there first. This makes sure a drop on the
/// session reaches UltimateRDP even if a version of mstscax registers its own target.
/// </summary>
/// <remarks>
/// The control window itself is registered by WinForms through <see cref="Control.AllowDrop"/>.
/// The child windows are created and destroyed by mstscax during a connection, so
/// <see cref="Refresh"/> is called again after each connection step.
/// </remarks>
internal sealed class ChildDropTargets
{
    private const int S_OK = 0;

    private readonly RdpControl _control;
    private readonly ChildWindowDropTarget _target;
    private readonly HashSet<IntPtr> _registered = [];

    public ChildDropTargets(RdpControl control)
    {
        _control = control;
        _target = new ChildWindowDropTarget(control);
    }

    /// <summary>
    /// Registers the target on the child windows that do not have it yet and logs the
    /// window tree with the previous registration state of each window.
    /// </summary>
    public void Refresh(string reason)
    {
        if (!_control.IsHandleCreated)
        {
            return;
        }

        Application.OleRequired();
        var root = _control.Handle;
        var children = NativeMethods.ChildWindows(root);
        _registered.IntersectWith(children);

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Drop targets ({reason}): control {NativeMethods.ClassName(root)} registered={NativeMethods.HasDropTarget(root)}"),
        };

        foreach (var hwnd in children)
        {
            var className = NativeMethods.ClassName(hwnd);
            if (_registered.Contains(hwnd))
            {
                lines.Add($"  {className}: already ours");
                continue;
            }

            var hadTarget = NativeMethods.HasDropTarget(hwnd);
            if (hadTarget)
            {
                NativeMethods.RevokeDragDrop(hwnd);
            }

            var hr = NativeMethods.RegisterDragDrop(hwnd, _target);
            if (hr == S_OK)
            {
                _registered.Add(hwnd);
            }

            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"  {className}: visible={NativeMethods.IsWindowVisible(hwnd)} hadTarget={hadTarget} register=0x{hr:X8}"));
        }

        ClientLog.Info(string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// Revokes every registration made by <see cref="Refresh"/>.
    /// </summary>
    public void RevokeAll()
    {
        foreach (var hwnd in _registered)
        {
            NativeMethods.RevokeDragDrop(hwnd);
        }

        _registered.Clear();
    }
}
