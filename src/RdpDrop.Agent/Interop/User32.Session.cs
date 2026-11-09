using System.Runtime.InteropServices;

namespace RdpDrop.Agent.Interop;

/// <summary>
/// Session functions (user32.dll): log off of the session the agent runs in.
/// </summary>
internal static partial class User32
{
    /// <summary>EWX_LOGOFF: ends the session of the calling process, with its applications.</summary>
    public const uint EwxLogoff = 0x00000000;

    /// <summary>
    /// EWX_FORCEIFHUNG: ends only the applications that do not answer. Applications with
    /// unsaved work still ask the user, as for a log off from the Start menu. EWX_FORCE is
    /// never used: it would lose that work.
    /// </summary>
    public const uint EwxForceIfHung = 0x00000010;

    /// <summary>SHTDN_REASON_MAJOR_OTHER: no particular reason category.</summary>
    public const uint ShtdnReasonMajorOther = 0x00000000;

    /// <summary>SHTDN_REASON_FLAG_PLANNED: the end of the session was asked for.</summary>
    public const uint ShtdnReasonFlagPlanned = 0x80000000;

    /// <summary>
    /// Asks Windows to log off, restart or shut down. With <see cref="EwxLogoff"/> it logs
    /// off the session of the calling process and needs no privilege. The call returns at
    /// once; the log off goes on in the background. Returns false on failure, with the
    /// error in <see cref="Marshal.GetLastPInvokeError"/>.
    /// </summary>
    /// <param name="uFlags">EWX_ flags, here <see cref="EwxLogoff"/> | <see cref="EwxForceIfHung"/>.</param>
    /// <param name="dwReason">SHTDN_REASON_ code recorded by Windows.</param>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ExitWindowsEx(uint uFlags, uint dwReason);
}
