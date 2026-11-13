using UltimateRDP.Core.Agent;
using UltimateRDP.Core.Display;

namespace UltimateRDP.Core.Tests.Agent;

public class ProxyBoundsTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1080);

    // A second monitor left of the primary one.
    private static readonly ScreenRect TwoMonitors = new(-1920, 0, 3840, 1080);

    [Fact]
    public void A_desktop_session_covers_the_virtual_screen()
    {
        Assert.Equal(TwoMonitors, ProxyBounds.For(false, 100, 100, TwoMonitors));
    }

    [Fact]
    public void A_remoteapp_session_gets_a_square_centered_on_the_point()
    {
        var bounds = ProxyBounds.For(true, 500, 400, Screen);

        Assert.Equal(new ScreenRect(484, 384, 32, 32), bounds);
        Assert.True(bounds.Contains(500, 400));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1919, 0, 1888, 0)]
    [InlineData(0, 1079, 0, 1048)]
    [InlineData(1919, 1079, 1888, 1048)]
    public void The_square_stays_inside_at_the_four_corners(int x, int y, int left, int top)
    {
        var bounds = ProxyBounds.For(true, x, y, Screen);

        Assert.Equal(new ScreenRect(left, top, 32, 32), bounds);
        Assert.True(bounds.Contains(x, y));
        Assert.True(bounds.X >= Screen.X && bounds.Right <= Screen.Right);
        Assert.True(bounds.Y >= Screen.Y && bounds.Bottom <= Screen.Bottom);
    }

    [Theory]
    [InlineData(-1920, 0)]
    [InlineData(-1, 500)]
    [InlineData(-1900, 1079)]
    public void The_square_stays_inside_with_a_negative_origin(int x, int y)
    {
        var bounds = ProxyBounds.For(true, x, y, TwoMonitors);

        Assert.True(bounds.Contains(x, y));
        Assert.True(bounds.X >= TwoMonitors.X && bounds.Right <= TwoMonitors.Right);
        Assert.True(bounds.Y >= TwoMonitors.Y && bounds.Bottom <= TwoMonitors.Bottom);
        Assert.Equal(32, bounds.Width);
    }

    [Fact]
    public void A_screen_smaller_than_the_square_is_covered()
    {
        var tiny = new ScreenRect(0, 0, 20, 10);

        Assert.Equal(tiny, ProxyBounds.For(true, 5, 5, tiny));
    }
}
