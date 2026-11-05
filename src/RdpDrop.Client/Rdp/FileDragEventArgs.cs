namespace RdpDrop.Client.Rdp;

/// <summary>
/// A drag of files over the session, or a drop of files on it.
/// </summary>
internal sealed class FileDragEventArgs : EventArgs
{
    public FileDragEventArgs(IReadOnlyList<string> files, Point clientPoint)
    {
        Files = files;
        ClientPoint = clientPoint;
    }

    /// <summary>Paths taken from the CF_HDROP data, in the order of the source.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>Cursor position in the client coordinates of the RDP control.</summary>
    public Point ClientPoint { get; }

    /// <summary>
    /// Effect reported to the drag source. The handler sets <see cref="DragDropEffects.Copy"/>
    /// to accept and leaves <see cref="DragDropEffects.None"/> to refuse.
    /// </summary>
    public DragDropEffects Effect { get; set; } = DragDropEffects.None;
}
