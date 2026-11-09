using System.Runtime.InteropServices;

namespace UltimateRDP.Agent.Interop;

/// <summary>
/// Console and process functions (kernel32.dll): command line output from this windowed
/// program, the session of the process, and the quiet end after an unexpected error.
/// </summary>
internal static partial class Kernel32
{
    /// <summary>ATTACH_PARENT_PROCESS: attach to the console of the parent process.</summary>
    public const int AttachParentProcess = -1;

    /// <summary>STD_OUTPUT_HANDLE.</summary>
    public const int StdOutputHandle = -11;

    /// <summary>Attaches the process to the console of another process. Returns false when there is none.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(int dwProcessId);

    /// <summary>Returns a standard handle; zero or -1 when the process has none.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GetStdHandle(int nStdHandle);

    /// <summary>Pseudo handle of the current process; it needs no closing.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GetCurrentProcess();

    /// <summary>
    /// Ends a process at once with the given exit code. Used on the current process after
    /// an unexpected error, so that Windows shows no error report.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    /// <summary>Gives the Remote Desktop session of a process.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);
}
