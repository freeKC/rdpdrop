using RdpDrop.Agent.Interop;

namespace RdpDrop.Agent.Drops;

/// <summary>
/// Invisible window that receives the simulated button press, so that the drag starts
/// from a window of the drag thread and OLE gets the mouse capture. It covers the whole
/// virtual screen, stays on top, never takes the focus and has no taskbar button. Its
/// opacity is 1 out of 255: invisible to the eye, but still hit by the mouse, which
/// passes through a layered window at opacity 0.
/// </summary>
internal sealed class ProxyWindow : Form
{
    public ProxyWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        TopMost = true;
        Text = "RdpDrop drag";
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style = User32.WsPopup;
            parameters.ExStyle |= User32.WsExLayered | User32.WsExToolWindow | User32.WsExNoActivate | User32.WsExTopmost;
            return parameters;
        }
    }

    /// <summary>Covers the virtual screen, in physical pixels since the agent is per monitor DPI aware.</summary>
    public void CoverVirtualScreen() => Bounds = SystemInformation.VirtualScreen;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        User32.SetLayeredWindowAttributes(Handle, 0, 1, User32.LwaAlpha);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case User32.WmMouseActivate:
                // A click must not bring the window to the foreground.
                m.Result = User32.MaNoActivate;
                return;

            case User32.WmDpiChanged:
                // The window spans every monitor; resizing it to one monitor would let the
                // press miss it.
                m.Result = IntPtr.Zero;
                return;

            default:
                base.WndProc(ref m);
                return;
        }
    }
}
