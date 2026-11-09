namespace UltimateRDP.Client.Rdp;

/// <summary>
/// Choices of the client that are applied to the control before a connection, on top of
/// the .rdp file.
/// </summary>
/// <param name="SmartSizing">True in "scale to window" mode, false in "resize session" mode.</param>
/// <param name="DesktopScaleFactor">Desktop scale factor in percent, 100 to 500.</param>
/// <param name="DeviceScaleFactor">Device scale factor: 100, 140 or 180.</param>
/// <param name="KeyboardHookMode">0 local, 1 remote, 2 remote in full screen only.</param>
/// <param name="UseMultimon">True to span every monitor in full screen.</param>
/// <param name="FullScreenTitle">Title of the full screen window of the control.</param>
internal sealed record SessionOptions(
    bool SmartSizing,
    int DesktopScaleFactor,
    int DeviceScaleFactor,
    int KeyboardHookMode,
    bool UseMultimon,
    string FullScreenTitle);
