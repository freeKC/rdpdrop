using UltimateRDP.Core.Display;

namespace UltimateRDP.Core.Tests.Display;

public class ScreenMappingTests
{
    private static readonly ScreenRect SingleMonitor = new(0, 0, 1920, 1080);

    // A second monitor left of the primary one: the virtual screen starts at a negative X.
    private static readonly ScreenRect MonitorOnTheLeft = new(-2560, 0, 4480, 1440);

    // A second monitor above the primary one.
    private static readonly ScreenRect MonitorAbove = new(0, -1080, 1920, 2160);

    [Fact]
    public void On_one_monitor_the_point_does_not_change()
    {
        Assert.Equal((412, 300), ScreenMapping.ToSessionPoint(412, 300, SingleMonitor));
    }

    [Fact]
    public void A_monitor_on_the_left_moves_the_origin()
    {
        Assert.Equal((2560 - 100, 50), ScreenMapping.ToSessionPoint(-100, 50, MonitorOnTheLeft));
        Assert.Equal((2560 + 10, 20), ScreenMapping.ToSessionPoint(10, 20, MonitorOnTheLeft));
    }

    [Fact]
    public void A_monitor_above_moves_the_origin()
    {
        Assert.Equal((5, 1080 - 30), ScreenMapping.ToSessionPoint(5, -30, MonitorAbove));
    }

    [Fact]
    public void The_corners_of_the_virtual_screen_map_to_its_corners()
    {
        Assert.Equal((0, 0), ScreenMapping.ToSessionPoint(-2560, 0, MonitorOnTheLeft));
        Assert.Equal((4479, 1439), ScreenMapping.ToSessionPoint(1919, 1439, MonitorOnTheLeft));
    }

    [Fact]
    public void A_box_with_the_right_edge_excluded_matches()
    {
        Assert.Equal(MonitorLayoutMatch.Matches, ScreenMapping.Compare(-2560, 0, 1920, 1440, MonitorOnTheLeft));
    }

    [Fact]
    public void A_box_with_the_right_edge_included_matches()
    {
        Assert.Equal(MonitorLayoutMatch.Matches, ScreenMapping.Compare(0, 0, 1919, 1079, SingleMonitor));
    }

    [Theory]
    [InlineData(0, 0, 4480, 1440)]
    [InlineData(-2560, 0, 1920, 1080)]
    [InlineData(-2560, 0, 1900, 1440)]
    [InlineData(0, -1080, 1920, 1080)]
    public void Another_layout_differs(int left, int top, int right, int bottom)
    {
        Assert.Equal(MonitorLayoutMatch.Differs, ScreenMapping.Compare(left, top, right, bottom, MonitorOnTheLeft));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(100, 0, 100, 50)]
    [InlineData(0, 50, 100, 10)]
    public void An_empty_box_says_nothing(int left, int top, int right, int bottom)
    {
        Assert.Equal(MonitorLayoutMatch.Unknown, ScreenMapping.Compare(left, top, right, bottom, SingleMonitor));
    }

    [Fact]
    public void A_rectangle_holds_its_top_left_pixel_and_not_its_right_edge()
    {
        var rect = ScreenRect.FromEdges(-10, -10, 10, 10);

        Assert.Equal(new ScreenRect(-10, -10, 20, 20), rect);
        Assert.True(rect.Contains(-10, -10));
        Assert.True(rect.Contains(9, 9));
        Assert.False(rect.Contains(10, 0));
        Assert.False(rect.Contains(0, 10));
    }

    [Fact]
    public void Edges_in_the_wrong_order_give_an_empty_rectangle()
    {
        Assert.True(ScreenRect.FromEdges(10, 10, 0, 0).IsEmpty);
    }
}
