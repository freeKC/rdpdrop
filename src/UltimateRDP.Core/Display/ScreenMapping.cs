namespace UltimateRDP.Core.Display;

/// <summary>
/// A rectangle of the screen in pixels, such as the virtual screen of the monitors or a
/// window. <see cref="X"/> and <see cref="Y"/> may be negative: a monitor left of or above
/// the primary one has negative coordinates.
/// </summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width, zero or more.</param>
/// <param name="Height">Height, zero or more.</param>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    /// <summary>The edge right of the last column, X plus Width.</summary>
    public int Right => X + Width;

    /// <summary>The edge below the last row, Y plus Height.</summary>
    public int Bottom => Y + Height;

    /// <summary>True when the rectangle covers no pixel.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>
    /// The rectangle between four edges, the right and bottom ones excluded, as Windows gives
    /// them in a RECT. Edges in the wrong order give an empty rectangle.
    /// </summary>
    public static ScreenRect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    /// <summary>True when the pixel at (<paramref name="x"/>, <paramref name="y"/>) is inside.</summary>
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <inheritdoc />
    public override string ToString() => FormattableString.Invariant($"{Width}x{Height} at ({X}, {Y})");
}

/// <summary>What the monitors of a RemoteApp session look like, compared with the local ones.</summary>
public enum MonitorLayoutMatch
{
    /// <summary>The control did not say, or gave an empty box.</summary>
    Unknown,

    /// <summary>The box of the remote monitors is the local virtual screen.</summary>
    Matches,

    /// <summary>The box of the remote monitors differs from the local virtual screen.</summary>
    Differs,
}

/// <summary>
/// Coordinates of a RemoteApp session. Its windows are local windows at the same place as on
/// the server, whose desktop copies the layout of the local monitors. A point of the local
/// screen is therefore the same point of the remote screen, and the protocol counts points
/// from the top left corner of the session desktop, which is the top left corner of the
/// virtual screen. The agent adds the origin of its own virtual screen back.
/// </summary>
public static class ScreenMapping
{
    /// <summary>
    /// The point of the protocol, counted from the top left corner of the session desktop,
    /// for the local screen point (<paramref name="screenX"/>, <paramref name="screenY"/>) in
    /// a RemoteApp session whose desktop is <paramref name="virtualScreen"/>.
    /// </summary>
    public static (int X, int Y) ToSessionPoint(int screenX, int screenY, ScreenRect virtualScreen) =>
        (screenX - virtualScreen.X, screenY - virtualScreen.Y);

    /// <summary>
    /// Compares the box of the remote monitors, as the control gives it with
    /// GetRemoteMonitorsBoundingBox, with the local virtual screen. The control may give the
    /// right and bottom edges included or excluded, so a difference of one pixel there still
    /// matches; the origin must be the same.
    /// </summary>
    public static MonitorLayoutMatch Compare(int left, int top, int right, int bottom, ScreenRect localVirtualScreen)
    {
        if (right <= left || bottom <= top)
        {
            return MonitorLayoutMatch.Unknown;
        }

        var sameOrigin = left == localVirtualScreen.X && top == localVirtualScreen.Y;
        var widthGap = Math.Abs(right - localVirtualScreen.Right);
        var heightGap = Math.Abs(bottom - localVirtualScreen.Bottom);
        return sameOrigin && widthGap <= 1 && heightGap <= 1 ? MonitorLayoutMatch.Matches : MonitorLayoutMatch.Differs;
    }
}
