using System.Security;
using Microsoft.Win32;

namespace UltimateRDP.Agent.Sessions;

/// <summary>
/// The SysProcs key of the Remote Desktop server, read only: the image names that a RemoteApp
/// session ignores when it looks for programs that keep it open. An administrator may list
/// the agent there, see the README. The agent never writes it.
/// </summary>
internal static class SysProcs
{
    /// <summary>The key under HKEY_LOCAL_MACHINE.</summary>
    public const string KeyPath = @"SYSTEM\CurrentControlSet\Control\Terminal Server\SysProcs";

    /// <summary>The name of the value an administrator adds for the agent.</summary>
    public const string AgentValueName = "UltimateRDP.Agent.exe";

    /// <summary>The full name of that value, for the messages.</summary>
    public static string AgentValuePath => @"HKEY_LOCAL_MACHINE\" + KeyPath + @"\" + AgentValueName;

    /// <summary>
    /// The names of the values of the key, an empty list when it does not exist, or null when
    /// it cannot be read, with the reason in <paramref name="error"/>.
    /// </summary>
    public static IReadOnlyList<string>? Names(out string? error)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: false);
            error = null;
            return key is null ? [] : key.GetValueNames();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// The data of the value of the agent: its text when it exists, null when it does not,
    /// with <paramref name="error"/> set when the key cannot be read.
    /// </summary>
    public static string? AgentValue(out string? error)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: false);
            error = null;
            if (key?.GetValue(AgentValueName) is not { } value)
            {
                return null;
            }

            return key.GetValueKind(AgentValueName) + " " + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            error = ex.Message;
            return null;
        }
    }
}
