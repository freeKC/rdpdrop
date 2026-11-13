using System.Reflection;
using System.Runtime.InteropServices;

namespace UltimateRDP.Agent.Setup;

/// <summary>
/// Tells whether a scheduled task exists, read only, through the Task Scheduler API. The
/// agent never creates, changes or runs a task; the README gives the administrator the
/// commands that start it in RemoteApp sessions.
/// </summary>
internal static class ScheduledTaskProbe
{
    /// <summary>The task the README creates to start the agent in every session.</summary>
    public const string AgentTaskPath = @"\UltimateRDP\Agent";

    private const int FileNotFound = unchecked((int)0x80070002);
    private const int PathNotFound = unchecked((int)0x80070003);

    /// <summary>
    /// True when the task exists, false when it does not, null when the Task Scheduler cannot
    /// say, with the reason in <paramref name="error"/>.
    /// </summary>
    public static bool? Exists(string path, out string? error)
    {
        object? service = null;
        object? root = null;
        object? task = null;
        error = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            service = Activator.CreateInstance(type)!;
            Invoke(service, "Connect", null, null, null, null);
            root = Invoke(service, "GetFolder", @"\");
            task = Invoke(root!, "GetTask", path);
            return task is not null;
        }
        catch (TargetInvocationException ex) when (ex.InnerException?.HResult is FileNotFound or PathNotFound)
        {
            return false;
        }
        catch (Exception ex) when (ex is TargetInvocationException or COMException or InvalidCastException or MissingMethodException or TypeLoadException)
        {
            var inner = ex.InnerException ?? ex;
            error = $"0x{inner.HResult:X8} {inner.Message}";
            return null;
        }
        finally
        {
            Release(task);
            Release(root);
            Release(service);
        }
    }

    private static object? Invoke(object target, string method, params object?[] args) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args, System.Globalization.CultureInfo.InvariantCulture);

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }
}
