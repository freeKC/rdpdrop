namespace UltimateRDP.Core.Display;

/// <summary>
/// Size and scale rules for the remote desktop, as accepted by the Remote Desktop
/// control and the display control channel of the server.
/// </summary>
public static class SessionDisplay
{
    /// <summary>Smallest desktop width or height, in pixels.</summary>
    public const int MinSize = 200;

    /// <summary>Largest desktop width or height, in pixels.</summary>
    public const int MaxSize = 8192;

    /// <summary>Scale factor of a monitor at 96 DPI, in percent.</summary>
    public const int DefaultScaleFactor = 100;

    private const int BaseDpi = 96;
    private const int MinPhysicalMillimeters = 10;
    private const int MaxPhysicalMillimeters = 10000;

    /// <summary>Desktop scale factors offered to the user, in percent.</summary>
    public static IReadOnlyList<int> DesktopScaleFactors { get; } = [100, 125, 150, 175, 200];

    /// <summary>
    /// Returns the entry of <see cref="DesktopScaleFactors"/> nearest to the scale of a
    /// monitor at <paramref name="dpi"/>. A tie goes to the smaller factor. A value of
    /// zero or less gives <see cref="DefaultScaleFactor"/>.
    /// </summary>
    public static int DesktopScaleFromDpi(int dpi)
    {
        if (dpi <= 0)
        {
            return DefaultScaleFactor;
        }

        var percent = dpi * 100.0 / BaseDpi;
        var nearest = DesktopScaleFactors[0];
        foreach (var factor in DesktopScaleFactors)
        {
            if (Math.Abs(factor - percent) < Math.Abs(nearest - percent))
            {
                nearest = factor;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Returns the device scale factor that goes with a desktop scale factor: 100 below
    /// 125, 140 from 125 to 150, 180 above. The control accepts only these three values.
    /// </summary>
    public static int DeviceScaleFor(int desktopScaleFactor) => desktopScaleFactor switch
    {
        < 125 => 100,
        <= 150 => 140,
        _ => 180,
    };

    /// <summary>
    /// Clamps a desktop size to <see cref="MinSize"/> and <see cref="MaxSize"/> and makes
    /// the width even, which the display control channel requires.
    /// </summary>
    public static (int Width, int Height) ClampSize(int width, int height) =>
        (Math.Clamp(width, MinSize, MaxSize) & ~1, Math.Clamp(height, MinSize, MaxSize));

    /// <summary>
    /// Returns the physical size in millimeters of a desktop of <paramref name="width"/>
    /// by <paramref name="height"/> pixels at <paramref name="dpi"/>, or (0, 0) when either
    /// side falls outside 10 to 10000 mm. The server ignores a physical size of zero.
    /// </summary>
    public static (int Width, int Height) PhysicalMillimeters(int width, int height, int dpi)
    {
        if (dpi <= 0)
        {
            return (0, 0);
        }

        var physicalWidth = (int)Math.Round(width * 25.4 / dpi);
        var physicalHeight = (int)Math.Round(height * 25.4 / dpi);
        return IsPhysicalValid(physicalWidth) && IsPhysicalValid(physicalHeight)
            ? (physicalWidth, physicalHeight)
            : (0, 0);
    }

    private static bool IsPhysicalValid(int millimeters) =>
        millimeters is >= MinPhysicalMillimeters and <= MaxPhysicalMillimeters;
}
