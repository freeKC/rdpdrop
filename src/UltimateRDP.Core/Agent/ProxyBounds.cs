using UltimateRDP.Core.Display;

namespace UltimateRDP.Core.Agent;

/// <summary>
/// Where the invisible proxy window of a drag goes. In a desktop session it covers the
/// whole virtual screen. In a RemoteApp session the server shows every visible window of the
/// session on the client as a window of its own, and a window over the whole screen would
/// appear there as one: the proxy is then a small square under the press point.
/// </summary>
public static class ProxyBounds
{
    /// <summary>The side of the square of a RemoteApp session, in physical pixels.</summary>
    public const int RemoteAppSide = 32;

    /// <summary>
    /// The bounds of the proxy for a press at (<paramref name="x"/>, <paramref name="y"/>),
    /// in screen coordinates of the session. In a RemoteApp session, a square of
    /// <see cref="RemoteAppSide"/> centered on the point and moved inside the virtual screen
    /// when the point is near an edge, so that the point is always inside it.
    /// </summary>
    public static ScreenRect For(bool remoteApp, int x, int y, ScreenRect virtualScreen)
    {
        if (!remoteApp || virtualScreen.IsEmpty)
        {
            return virtualScreen;
        }

        var width = Math.Min(RemoteAppSide, virtualScreen.Width);
        var height = Math.Min(RemoteAppSide, virtualScreen.Height);
        var left = Math.Clamp(x - (width / 2), virtualScreen.X, virtualScreen.Right - width);
        var top = Math.Clamp(y - (height / 2), virtualScreen.Y, virtualScreen.Bottom - height);
        return new ScreenRect(left, top, width, height);
    }
}
