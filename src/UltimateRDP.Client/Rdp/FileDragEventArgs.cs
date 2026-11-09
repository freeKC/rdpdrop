using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Client.Rdp;

/// <summary>
/// A drag of files over the session, or a drop of files on it.
/// </summary>
internal sealed class FileDragEventArgs : EventArgs
{
    public FileDragEventArgs(IReadOnlyList<string> files, Point clientPoint, int keyState = KeyStates.None)
    {
        Files = files;
        ClientPoint = clientPoint;
        KeyState = keyState;
    }

    /// <summary>Paths taken from the CF_HDROP data, in the order of the source.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>
    /// Cursor position relative to the area that shows the session: the client area of the
    /// control, or the full screen area while the control runs full screen.
    /// </summary>
    public Point ClientPoint { get; }

    /// <summary>Modifier keys held during the drag, as <see cref="KeyStates"/> flags.</summary>
    public int KeyState { get; }

    /// <summary>
    /// Effect reported to the drag source. The handler sets <see cref="DragDropEffects.Copy"/>
    /// to accept and leaves <see cref="DragDropEffects.None"/> to refuse.
    /// </summary>
    public DragDropEffects Effect { get; set; } = DragDropEffects.None;

    /// <summary>
    /// Converts the OLE key state (MK_SHIFT 4, MK_CONTROL 8, MK_ALT 32), which is also the
    /// one of <see cref="DragEventArgs.KeyState"/>, to <see cref="KeyStates"/> flags.
    /// </summary>
    public static int FromOleKeyState(int oleKeyState)
    {
        var keys = KeyStates.None;
        if ((oleKeyState & 8) != 0)
        {
            keys |= KeyStates.Control;
        }

        if ((oleKeyState & 4) != 0)
        {
            keys |= KeyStates.Shift;
        }

        if ((oleKeyState & 32) != 0)
        {
            keys |= KeyStates.Alt;
        }

        return keys;
    }
}
