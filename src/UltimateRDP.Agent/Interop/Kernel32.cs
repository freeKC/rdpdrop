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

    /// <summary>Detaches the process from its console. Returns false when it has none.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FreeConsole();

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

    /// <summary>DUPLICATE_SAME_ACCESS: the copy has the access of the source handle.</summary>
    public const int DuplicateSameAccess = 2;

    /// <summary>ERROR_IO_PENDING: an overlapped operation goes on in the background.</summary>
    public const int ErrorIoPending = 997;

    /// <summary>ERROR_IO_INCOMPLETE: an overlapped operation is not over yet.</summary>
    public const int ErrorIoIncomplete = 996;

    /// <summary>ERROR_OPERATION_ABORTED: an overlapped operation was cancelled.</summary>
    public const int ErrorOperationAborted = 995;

    /// <summary>Makes a handle of the same object that the caller owns and closes.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwOptions);

    /// <summary>Reads from a file handle; with an OVERLAPPED, the read may go on in the background.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool ReadFile(IntPtr hFile, byte* lpBuffer, uint nNumberOfBytesToRead, uint* lpNumberOfBytesRead, NativeOverlapped* lpOverlapped);

    /// <summary>The result of an overlapped operation; waits for it when <paramref name="bWait"/> is true.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetOverlappedResult(IntPtr hFile, NativeOverlapped* lpOverlapped, out uint lpNumberOfBytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool bWait);

    /// <summary>Cancels an overlapped operation of the handle.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool CancelIoEx(IntPtr hFile, NativeOverlapped* lpOverlapped);

    /// <summary>Closes a handle.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);
}
