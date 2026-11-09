using System.Runtime.InteropServices;

namespace UltimateRDP.Agent.Interop;

/// <summary>
/// Console functions (kernel32.dll), used to print command line results from this
/// windowed program.
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
}
