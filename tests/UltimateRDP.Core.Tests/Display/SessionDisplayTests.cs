using UltimateRDP.Core.Display;

namespace UltimateRDP.Core.Tests.Display;

public class SessionDisplayTests
{
    [Theory]
    [InlineData(96, 100)]
    [InlineData(120, 125)]
    [InlineData(144, 150)]
    [InlineData(168, 175)]
    [InlineData(192, 200)]
    [InlineData(110, 125)]
    [InlineData(100, 100)]
    [InlineData(132, 125)]
    [InlineData(288, 200)]
    [InlineData(72, 100)]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    public void DesktopScaleFromDpi_rounds_to_the_nearest_offered_factor(int dpi, int expected)
    {
        Assert.Equal(expected, SessionDisplay.DesktopScaleFromDpi(dpi));
    }

    [Fact]
    public void DesktopScaleFromDpi_gives_the_smaller_factor_on_a_tie()
    {
        // 108 DPI is 112.5 %, half way between 100 and 125.
        Assert.Equal(100, SessionDisplay.DesktopScaleFromDpi(108));
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(124, 100)]
    [InlineData(125, 140)]
    [InlineData(150, 140)]
    [InlineData(160, 180)]
    [InlineData(175, 180)]
    [InlineData(200, 180)]
    [InlineData(500, 180)]
    public void DeviceScaleFor_maps_to_100_140_or_180(int desktop, int expected)
    {
        Assert.Equal(expected, SessionDisplay.DeviceScaleFor(desktop));
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1921, 1081, 1920, 1081)]
    [InlineData(100, 50, 200, 200)]
    [InlineData(9000, 9000, 8192, 8192)]
    [InlineData(201, 201, 200, 201)]
    [InlineData(0, -5, 200, 200)]
    public void ClampSize_clamps_and_makes_the_width_even(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), SessionDisplay.ClampSize(width, height));
    }

    [Fact]
    public void ClampSize_results_are_always_valid()
    {
        for (var width = 0; width < 9000; width += 37)
        {
            var (w, h) = SessionDisplay.ClampSize(width, width);

            Assert.Equal(0, w % 2);
            Assert.InRange(w, SessionDisplay.MinSize, SessionDisplay.MaxSize);
            Assert.InRange(h, SessionDisplay.MinSize, SessionDisplay.MaxSize);
        }
    }

    [Theory]
    [InlineData(1920, 1080, 96, 508, 286)]
    [InlineData(2880, 1620, 144, 508, 286)]
    [InlineData(200, 200, 96, 53, 53)]
    public void PhysicalMillimeters_converts_pixels(int width, int height, int dpi, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), SessionDisplay.PhysicalMillimeters(width, height, dpi));
    }

    [Theory]
    [InlineData(20, 1000, 96)]
    [InlineData(8192, 8192, 10)]
    [InlineData(1920, 1080, 0)]
    public void PhysicalMillimeters_is_zero_when_out_of_range(int width, int height, int dpi)
    {
        Assert.Equal((0, 0), SessionDisplay.PhysicalMillimeters(width, height, dpi));
    }

    [Fact]
    public void Offered_factors_are_the_five_steps()
    {
        Assert.Equal([100, 125, 150, 175, 200], SessionDisplay.DesktopScaleFactors);
    }
}
