using System.Security;
using Microsoft.Win32;

namespace RdpDrop.Agent.Setup;

/// <summary>
/// Adds or removes the Run value that starts the agent with each user session:
/// under HKEY_CURRENT_USER for the current user, under HKEY_LOCAL_MACHINE for every user.
/// </summary>
internal static class StartupRegistration
{
    /// <summary>Command stored in the Run value: the quoted path of this executable.</summary>
    public static string Command => $"\"{ExecutablePath}\"";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("The path of the executable is unknown.");

    /// <summary>Writes the Run value. Returns null on success, or the reason of the failure.</summary>
    public static string? Register(RegistryKey hive)
    {
        try
        {
            using var key = hive.CreateSubKey(AgentInfo.RunKeyPath, writable: true);
            key.SetValue(AgentInfo.RunValueName, Command, RegistryValueKind.String);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Removes the Run value. Returns null on success or when there was nothing to
    /// remove, or the reason of the failure.
    /// </summary>
    public static string? Unregister(RegistryKey hive)
    {
        try
        {
            using var key = hive.OpenSubKey(AgentInfo.RunKeyPath, writable: false);
            if (key?.GetValue(AgentInfo.RunValueName) is null)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return ex.Message;
        }

        try
        {
            using var key = hive.OpenSubKey(AgentInfo.RunKeyPath, writable: true);
            key?.DeleteValue(AgentInfo.RunValueName, throwOnMissingValue: false);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return ex.Message;
        }
    }

    /// <summary>True when the Run value exists in <paramref name="hive"/>.</summary>
    public static bool IsRegistered(RegistryKey hive)
    {
        try
        {
            using var key = hive.OpenSubKey(AgentInfo.RunKeyPath, writable: false);
            return key?.GetValue(AgentInfo.RunValueName) is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }
}
