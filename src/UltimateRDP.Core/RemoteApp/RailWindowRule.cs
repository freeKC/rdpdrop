namespace UltimateRDP.Core.RemoteApp;

/// <summary>What a top level window of the client process is, for the windows of a RemoteApp program.</summary>
public enum RailWindowKind
{
    /// <summary>A window of the program on the server: it takes drops and counts as an open window of the program.</summary>
    Program,

    /// <summary>A window of the Remote Desktop control itself, such as its UIMainClass container.</summary>
    Control,

    /// <summary>The window that the agent shows under the point while it replays a drop.</summary>
    DropProxy,

    /// <summary>A window of the server that cannot be activated, such as a tooltip or an overlay.</summary>
    NotActivatable,

    /// <summary>A window of another class: not one that the control creates for a program.</summary>
    OtherClass,
}

/// <summary>
/// Tells which top level windows of the client process are the windows of a RemoteApp
/// program, since 0.9.0. The first real run of 0.8.0 registered drop targets on the
/// UIMainClass container of the control and on the 32 pixel proxy of the agent, a
/// RAIL_WINDOW with WS_EX_NOACTIVATE; neither is a window of the program.
/// </summary>
public static class RailWindowRule
{
    /// <summary>The class the control gives every window it shows for a RemoteApp program.</summary>
    public const string ProgramClass = "RAIL_WINDOW";

    /// <summary>The title of the proxy window of the agent, which the server sends with the window.</summary>
    public const string ProxyTitle = "UltimateRDP drag";

    /// <summary>WS_EX_NOACTIVATE: a window that the user cannot activate.</summary>
    public const long ExNoActivate = 0x08000000;

    /// <summary>The classes of the windows of the control itself, and of mstsc for the same parts.</summary>
    private static readonly HashSet<string> ControlClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "UIMainClass",
        "UIContainerClass",
        "IHWindowClass",
        "OPWindowClass",
        "OPContainerClass",
        "BBarWindowClass",
        "TscShellContainerClass",
        "TscShellAxHostClass",
    };

    /// <summary>Classifies a window from its class, its extended style and its title.</summary>
    public static RailWindowKind Classify(string? className, long exStyle, string? title)
    {
        if (className is not null && ControlClasses.Contains(className))
        {
            return RailWindowKind.Control;
        }

        if (!string.Equals(className, ProgramClass, StringComparison.Ordinal))
        {
            return RailWindowKind.OtherClass;
        }

        if (string.Equals(title, ProxyTitle, StringComparison.Ordinal))
        {
            return RailWindowKind.DropProxy;
        }

        return (exStyle & ExNoActivate) != 0 ? RailWindowKind.NotActivatable : RailWindowKind.Program;
    }

    /// <summary>True for a window of the program, see <see cref="Classify"/>.</summary>
    public static bool IsProgramWindow(string? className, long exStyle, string? title) =>
        Classify(className, exStyle, title) == RailWindowKind.Program;
}
