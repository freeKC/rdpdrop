namespace UltimateRDP.Client.Settings;

/// <summary>
/// How the session follows the size of the window.
/// </summary>
internal enum DisplayMode
{
    /// <summary>The session resolution follows the window (dynamic resolution).</summary>
    ResizeSession,

    /// <summary>The session keeps its resolution and the picture is scaled (smart sizing).</summary>
    ScaleToWindow,
}
