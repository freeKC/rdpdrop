using System.Globalization;
using System.Runtime.InteropServices;
using RdpDrop.Client.Logging;
using RdpDrop.Core.Credentials;

namespace RdpDrop.Client.Sessions;

/// <summary>
/// Reads and deletes the credentials that Windows keeps for a Remote Desktop host in the
/// Credential Manager, under the target <c>TERMSRV/&lt;host&gt;</c>. RdpDrop never writes
/// one: Windows saves it when the user ticks "Remember me" in its credential prompt, and
/// <c>cmdkey /generic:TERMSRV/&lt;host&gt;</c> creates one by hand.
/// </summary>
/// <remarks>
/// Only a generic credential gives its password to an application. A credential of type
/// "domain password" can be listed and deleted, but its password is reserved to the
/// Windows authentication packages, so RdpDrop does not use it.
/// Nothing read here is ever logged except the host name.
/// </remarks>
internal static class SavedCredentials
{
    private const uint CredTypeGeneric = 1;
    private const uint CredTypeDomainPassword = 2;
    private const int ErrorNotFound = 1168;

    /// <summary>Credential Manager target of <paramref name="host"/>.</summary>
    public static string TargetFor(string host) => "TERMSRV/" + host;

    /// <summary>
    /// Reads the generic credential of <paramref name="host"/>, or returns null when
    /// there is none or it cannot be read. The caller disposes the result as soon as
    /// the password is applied.
    /// </summary>
    public static SavedCredential? Read(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        if (!NativeMethods.CredRead(TargetFor(host), CredTypeGeneric, 0, out var pointer))
        {
            LogFailure("read", host, Marshal.GetLastPInvokeError());
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(pointer);
            var (userName, domain) = AccountName.Split(Marshal.PtrToStringUni(credential.UserName));
            var password = new char[credential.CredentialBlobSize / sizeof(char)];
            try
            {
                if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize > 0)
                {
                    Marshal.Copy(credential.CredentialBlob, password, 0, password.Length);

                    // The buffer belongs to this process until CredFree; clear it first.
                    Marshal.Copy(new byte[credential.CredentialBlobSize], 0, credential.CredentialBlob, (int)credential.CredentialBlobSize);
                }

                return new SavedCredential(userName, domain, password);
            }
            catch
            {
                Array.Clear(password);
                throw;
            }
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    /// <summary>
    /// Tells which credentials Windows keeps for <paramref name="host"/>, without reading
    /// any secret.
    /// </summary>
    public static SavedCredentialKinds Find(string host)
    {
        var kinds = SavedCredentialKinds.None;
        if (string.IsNullOrWhiteSpace(host))
        {
            return kinds;
        }

        if (Exists(host, CredTypeGeneric))
        {
            kinds |= SavedCredentialKinds.Generic;
        }

        if (Exists(host, CredTypeDomainPassword))
        {
            kinds |= SavedCredentialKinds.DomainPassword;
        }

        return kinds;
    }

    /// <summary>
    /// Deletes every credential Windows keeps for <paramref name="host"/>. Returns true
    /// when at least one was deleted.
    /// </summary>
    public static bool Delete(string host)
    {
        var deleted = false;
        foreach (var type in (uint[])[CredTypeGeneric, CredTypeDomainPassword])
        {
            if (NativeMethods.CredDelete(TargetFor(host), type, 0))
            {
                deleted = true;
                ClientLog.Info(string.Create(CultureInfo.InvariantCulture, $"Saved credential of type {type} for {host} deleted."));
            }
            else
            {
                LogFailure("delete", host, Marshal.GetLastPInvokeError());
            }
        }

        return deleted;
    }

    private static bool Exists(string host, uint type)
    {
        if (!NativeMethods.CredRead(TargetFor(host), type, 0, out var pointer))
        {
            LogFailure("look up", host, Marshal.GetLastPInvokeError());
            return false;
        }

        // Domain passwords come without their secret; a generic one is cleared before
        // it is freed.
        var credential = Marshal.PtrToStructure<NativeMethods.Credential>(pointer);
        if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize > 0)
        {
            Marshal.Copy(new byte[credential.CredentialBlobSize], 0, credential.CredentialBlob, (int)credential.CredentialBlobSize);
        }

        NativeMethods.CredFree(pointer);
        return true;
    }

    private static void LogFailure(string action, string host, int error)
    {
        if (error != ErrorNotFound)
        {
            ClientLog.Warn(string.Create(CultureInfo.InvariantCulture, $"Cannot {action} the saved credential for {host} (error {error})."));
        }
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredDelete(string targetName, uint type, uint flags);

        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr buffer);

        /// <summary>CREDENTIALW. Only the members read by RdpDrop are named for use.</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct Credential
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }
    }
}

/// <summary>Kinds of credentials found for a host.</summary>
[Flags]
internal enum SavedCredentialKinds
{
    None = 0,

    /// <summary>A generic credential, whose password RdpDrop can apply.</summary>
    Generic = 1,

    /// <summary>A domain password credential, whose password only Windows can read.</summary>
    DomainPassword = 2,
}

/// <summary>
/// A credential read from the Credential Manager. The password is held in an array that
/// <see cref="Dispose"/> clears.
/// </summary>
internal sealed class SavedCredential : IDisposable
{
    private readonly char[] _password;

    public SavedCredential(string userName, string domain, char[] password)
    {
        UserName = userName;
        Domain = domain;
        _password = password;
    }

    public string UserName { get; }

    public string Domain { get; }

    /// <summary>
    /// Hands the password to <paramref name="use"/> as a string, then overwrites that
    /// string and the array. A copy made by the callee, such as the BSTR that COM
    /// interop passes to the control, is out of reach.
    /// </summary>
    public void UsePassword(Action<string> use)
    {
        if (_password.Length == 0)
        {
            use(string.Empty);
            return;
        }

        var text = new string(_password);
        Array.Clear(_password);
        try
        {
            use(text);
        }
        finally
        {
            // A string made from a non empty array is a fresh object, never interned,
            // so overwriting it touches nothing else.
            MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(text.AsSpan()), text.Length).Clear();
        }
    }

    public void Dispose() => Array.Clear(_password);
}
