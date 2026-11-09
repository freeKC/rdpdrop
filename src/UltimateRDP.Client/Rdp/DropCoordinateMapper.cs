namespace UltimateRDP.Client.Rdp;

/// <summary>
/// Converts a point of the RDP control to a pixel of the remote desktop.
/// </summary>
/// <remarks>
/// In "scale to window" mode smart sizing stretches the remote desktop over the whole
/// view, so each axis is scaled by the ratio between the desktop size and the view size.
/// In "resize session" mode the session follows the view and the ratio is 1:1 once the
/// size change event of the control has arrived. In full screen the view is the area
/// covered by the full screen window.
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
