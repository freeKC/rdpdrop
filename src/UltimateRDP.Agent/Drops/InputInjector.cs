using System.Runtime.InteropServices;
using UltimateRDP.Agent.Interop;

namespace UltimateRDP.Agent.Drops;

/// <summary>
/// Simulated mouse input for the drag source: absolute moves, and the press and release of
/// the logical left button. Nothing else is ever injected: no other button and no key.
/// Callable from any thread; calls are serialised so that a move never splits a
/// "move then release" pair.
/// </summary>
internal static class InputInjector
{
    private static readonly Lock Gate = new();

    /// <summary>
    /// Converts a point of the session desktop, as sent by the client (zero at the top
    /// left corner of the remote desktop), to a screen point. The virtual screen origin
    /// is negative when a monitor sits left of or above the primary one.
    /// </summary>
    public static User32.Point ToScreen(int x, int y)
    {
        var originX = User32.GetSystemMetrics(User32.SmXVirtualScreen);
        var originY = User32.GetSystemMetrics(User32.SmYVirtualScreen);
        var width = User32.GetSystemMetrics(User32.SmCxVirtualScreen);
        var height = User32.GetSystemMetrics(User32.SmCyVirtualScreen);
        return new User32.Point
        {
            X = originX + Math.Clamp(x, 0, Math.Max(0, width - 1)),
            Y = originY + Math.Clamp(y, 0, Math.Max(0, height - 1)),
        };
    }

    /// <summary>Moves the cursor to a screen point. Returns false when the input was blocked.</summary>
    public static bool MoveTo(User32.Point point)
    {
        lock (Gate)
        {
            var sent = Send([Move(point)]);
            Correct(point);
            return sent;
        }
    }

    /// <summary>Moves the cursor to a screen point, then presses the logical left button.</summary>
    public static bool PressAt(User32.Point point)
    {
        lock (Gate)
        {
            return Send([Move(point), Button(down: true)]);
        }
    }

    /// <summary>Moves the cursor to a screen point, then releases the logical left button, in one call.</summary>
    public static bool ReleaseAt(User32.Point point)
    {
        lock (Gate)
        {
            return Send([Move(point), Button(down: false)]);
        }
    }

    /// <summary>Releases the logical left button where the cursor is.</summary>
    public static bool Release()
    {
        lock (Gate)
        {
            return Send([Button(down: false)]);
        }
    }

    /// <summary>Current cursor position, or the given point when it cannot be read.</summary>
    public static User32.Point CursorOr(User32.Point fallback) =>
        User32.GetCursorPos(out var point) ? point : fallback;

    /// <summary>
    /// The rounding of absolute coordinates can land one pixel off; SetCursorPos puts the
    /// cursor exactly on the point. It sends no mouse event, so it cannot click.
    /// </summary>
    private static void Correct(User32.Point point)
    {
        if (User32.GetCursorPos(out var actual) && (actual.X != point.X || actual.Y != point.Y))
        {
            User32.SetCursorPos(point.X, point.Y);
        }
    }

    private static User32.Input Move(User32.Point point)
    {
        var originX = User32.GetSystemMetrics(User32.SmXVirtualScreen);
        var originY = User32.GetSystemMetrics(User32.SmYVirtualScreen);
        var width = User32.GetSystemMetrics(User32.SmCxVirtualScreen);
        var height = User32.GetSystemMetrics(User32.SmCyVirtualScreen);
        return new User32.Input
        {
            Type = User32.InputMouse,
            Mouse = new User32.MouseInput
            {
                Dx = Normalize(point.X, originX, width),
                Dy = Normalize(point.Y, originY, height),
                Flags = User32.MouseEventMove | User32.MouseEventAbsolute | User32.MouseEventVirtualDesk,
            },
        };
    }

    /// <summary>
    /// The logical left button. When the buttons are swapped for a left handed user,
    /// the logical left button is the physical right one.
    /// </summary>
    private static User32.Input Button(bool down)
    {
        var swapped = User32.GetSystemMetrics(User32.SmSwapButton) != 0;
        var flag = (swapped, down) switch
        {
            (false, true) => User32.MouseEventLeftDown,
            (false, false) => User32.MouseEventLeftUp,
            (true, true) => User32.MouseEventRightDown,
            (true, false) => User32.MouseEventRightUp,
        };

        return new User32.Input { Type = User32.InputMouse, Mouse = new User32.MouseInput { Flags = flag } };
    }

    /// <summary>
    /// Maps a pixel to 0..65535 over the virtual screen, rounding up so that the result
    /// falls inside the pixel and not on the edge of the previous one.
    /// </summary>
    private static int Normalize(int value, int origin, int extent)
    {
        if (extent <= 0)
        {
            return 0;
        }

        var scaled = ((((long)value - origin) << 16) + extent - 1) / extent;
        return (int)Math.Clamp(scaled, 0, 65535);
    }

    private static unsafe bool Send(User32.Input[] inputs)
    {
        fixed (User32.Input* pointer = inputs)
        {
            var sent = User32.SendInput((uint)inputs.Length, pointer, Marshal.SizeOf<User32.Input>());
            return sent == inputs.Length;
        }
    }
}
