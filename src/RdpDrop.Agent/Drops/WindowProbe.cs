using System.ComponentModel;
using System.Diagnostics;
using RdpDrop.Agent.Interop;
using RdpDrop.Core.Protocol;

namespace RdpDrop.Agent.Drops;

/// <summary>
/// Describes the window under a screen point, for the drop result and the log.
/// </summary>
internal static class WindowProbe
{
    /// <summary>
    /// Class and title of the top level window under <paramref name="point"/> (the title
    /// of its owner when it has none), the class of the window right under the point and
    /// the process name. Null when there is no window.
    /// </summary>
    public static TargetWindowInfo? Describe(User32.Point point)
    {
        var hit = User32.WindowFromPoint(point);
        if (hit == IntPtr.Zero)
        {
            return null;
        }

        var root = User32.GetAncestor(hit, User32.GaRoot);
        if (root == IntPtr.Zero)
        {
            root = hit;
        }

        var title = User32.GetWindowText(root);
        if (title.Length == 0)
        {
            var owner = User32.GetAncestor(hit, User32.GaRootOwner);
            if (owner != IntPtr.Zero && owner != root)
            {
                title = User32.GetWindowText(owner);
            }
        }

        var rootClass = User32.GetClassName(root);
        var hitClass = User32.GetClassName(hit);
        return new TargetWindowInfo(
            rootClass,
            title.Length == 0 ? null : title,
            hit == root || hitClass == rootClass ? null : hitClass,
            ProcessName(root));
    }

    private static string? ProcessName(IntPtr window)
    {
        User32.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }
}
