namespace RdpDrop.Client.Rdp;

/// <summary>
/// Converts a point of the RDP control to a pixel of the remote desktop.
/// </summary>
/// <remarks>
/// Smart sizing stretches the remote desktop over the whole control, so each axis is
/// scaled by the ratio between the desktop size and the control size.
/// </remarks>
internal static class DropCoordinateMapper
{
    public static Point ToRemote(Point clientPoint, Size controlSize, Size desktopSize)
    {
        if (controlSize.Width <= 0 || controlSize.Height <= 0 || desktopSize.Width <= 0 || desktopSize.Height <= 0)
        {
            return Point.Empty;
        }

        var x = (int)((long)clientPoint.X * desktopSize.Width / controlSize.Width);
        var y = (int)((long)clientPoint.Y * desktopSize.Height / controlSize.Height);
        return new Point(
            Math.Clamp(x, 0, desktopSize.Width - 1),
            Math.Clamp(y, 0, desktopSize.Height - 1));
    }
}
