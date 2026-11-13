using System.Runtime.InteropServices;

namespace UltimateRDP.Agent.Interop;

/// <summary>
/// The processes of a session, from the Remote Desktop Services API (wtsapi32.dll).
/// </summary>
internal static partial class WtsApi
{
    /// <summary>WTSTypeProcessInfoLevel0 of WTS_TYPE_CLASS: a buffer of WTS_PROCESS_INFOW.</summary>
    private const int TypeProcessInfoLevel0 = 0;

    /// <summary>WTS_PROCESS_INFOW: one process of the list.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public uint SessionId;
        public uint ProcessId;
        public IntPtr ProcessName;
        public IntPtr UserSid;
    }

    /// <summary>
    /// Lists the processes of one session. Level 0 gives WTS_PROCESS_INFOW. Free the buffer
    /// with WTSFreeMemoryExW and the same count.
    /// </summary>
    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSEnumerateProcessesExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSEnumerateProcessesEx(IntPtr hServer, ref uint pLevel, uint sessionId, out IntPtr ppProcessInfo, out uint pCount);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSFreeMemoryExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSFreeMemoryEx(int typeClass, IntPtr memory, uint numberOfEntries);

    /// <summary>
    /// The image names of the processes of session <paramref name="sessionId"/>, such as
    /// "rdpshell.exe", in one call to the Remote Desktop service. Null when the call fails,
    /// with the Windows error in <paramref name="error"/>. A user lists the processes of their
    /// own session without any right of an administrator.
    /// </summary>
    public static List<string>? SessionProcessNames(uint sessionId, out int error)
    {
        uint level = 0;
        if (!WTSEnumerateProcessesEx(CurrentServerHandle, ref level, sessionId, out var buffer, out var count))
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }

        error = 0;
        try
        {
            var names = new List<string>((int)count);
            var size = Marshal.SizeOf<ProcessInfo>();
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<ProcessInfo>(buffer + (i * size));
                if (entry.SessionId == sessionId && Marshal.PtrToStringUni(entry.ProcessName) is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }

            return names;
        }
        finally
        {
            WTSFreeMemoryEx(TypeProcessInfoLevel0, buffer, count);
        }
    }
}
