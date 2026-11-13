using System.Runtime.InteropServices;

namespace UltimateRDP.Agent.Interop;

/// <summary>
/// Network connections of the logon session (mpr.dll): the drive letters mapped to shares,
/// as <c>net use</c> makes them.
/// </summary>
internal static unsafe partial class Mpr
{
    /// <summary>NO_ERROR.</summary>
    public const int NoError = 0;

    /// <summary>ERROR_NOT_CONNECTED: the letter is not a network connection.</summary>
    public const int ErrorNotConnected = 2250;

    /// <summary>ERROR_MORE_DATA: the buffer is too small.</summary>
    public const int ErrorMoreData = 234;

    /// <summary>RESOURCETYPE_DISK.</summary>
    private const int ResourceTypeDisk = 1;

    /// <summary>CONNECT_TEMPORARY: the connection is not remembered, and goes with the logon session.</summary>
    private const int ConnectTemporary = 0x00000004;

    /// <summary>NETRESOURCEW, with its strings as pointers so that the structure stays blittable.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public char* LocalName;
        public char* RemoteName;
        public char* Comment;
        public char* Provider;
    }

    [LibraryImport("mpr.dll", EntryPoint = "WNetAddConnection2W")]
    private static partial int WNetAddConnection2(NetResource* netResource, char* password, char* userName, int flags);

    [LibraryImport("mpr.dll", EntryPoint = "WNetCancelConnection2W")]
    private static partial int WNetCancelConnection2(char* name, int flags, int force);

    [LibraryImport("mpr.dll", EntryPoint = "WNetGetConnectionW")]
    private static partial int WNetGetConnection(char* localName, char* remoteName, ref int length);

    /// <summary>
    /// Maps <paramref name="localName"/>, such as <c>Z:</c>, to <paramref name="remoteName"/>
    /// for this logon session only: not remembered, with the credentials of the session and
    /// no password. Returns a Windows error code, <see cref="NoError"/> on success.
    /// </summary>
    public static int AddTemporaryConnection(string localName, string remoteName)
    {
        fixed (char* local = localName)
        fixed (char* remote = remoteName)
        {
            var resource = new NetResource { Type = ResourceTypeDisk, LocalName = local, RemoteName = remote };
            return WNetAddConnection2(&resource, null, null, ConnectTemporary);
        }
    }

    /// <summary>
    /// Removes the mapping of <paramref name="localName"/>; with <paramref name="force"/>, even
    /// when files are open on it. Never changes a remembered connection of the profile.
    /// Returns a Windows error code.
    /// </summary>
    public static int CancelConnection(string localName, bool force)
    {
        fixed (char* local = localName)
        {
            return WNetCancelConnection2(local, 0, force ? 1 : 0);
        }
    }

    /// <summary>
    /// The share <paramref name="localName"/> is mapped to, with the Windows code of the
    /// answer: <see cref="NoError"/> for a mapping, <see cref="ErrorNotConnected"/> for a
    /// letter that is not one, another code for a remembered connection that is not available
    /// or an error.
    /// </summary>
    public static string? GetConnection(string localName, out int result)
    {
        var length = 260;
        while (true)
        {
            var buffer = new char[length];
            fixed (char* local = localName)
            fixed (char* remote = buffer)
            {
                result = WNetGetConnection(local, remote, ref length);
            }

            if (result == ErrorMoreData && length > buffer.Length && length < 32768)
            {
                continue;
            }

            return result == NoError ? new string(buffer).TrimEnd('\0') : null;
        }
    }
}
