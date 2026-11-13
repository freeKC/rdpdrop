using System.Reflection;

namespace UltimateRDP.Agent;

/// <summary>
/// Fixed facts about the agent: version, names and file locations.
/// </summary>
internal static class AgentInfo
{
    public const string ProductName = "UltimateRDP agent";

    public const string ProjectUrl = "https://github.com/freeKC/ultimaterdp";

    public const string Publisher = "APROGSYS";

    public const string PublisherUrl = "https://www.aprogsys.com/";

    /// <summary>Per session mutex that keeps a single agent in each user session.</summary>
    public const string InstanceMutexName = @"Local\UltimateRDP.Agent";

    /// <summary>Per session event that asks the running agent to exit.</summary>
    public const string ExitEventName = @"Local\UltimateRDP.Agent.Exit";

    /// <summary>Name of the value written under the Run key.</summary>
    public const string RunValueName = "UltimateRDP";

    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Version without the source revision suffix, for example "0.2.1".</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>%LOCALAPPDATA%\UltimateRDP</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UltimateRDP");

    /// <summary>%LOCALAPPDATA%\UltimateRDP\agent.json</summary>
    public static string SettingsPath { get; } = Path.Combine(DataDirectory, "agent.json");

    /// <summary>
    /// %ProgramData%\UltimateRDP\agent.json: machine wide defaults that an administrator may
    /// write; each key of the file of the user wins over them. The agent only reads it.
    /// </summary>
    public static string MachineSettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UltimateRDP",
        "agent.json");

    /// <summary>%LOCALAPPDATA%\UltimateRDP\mapped-drives.json: the drive letters this agent mapped, for the next start after a crash.</summary>
    public static string MappedDrivesPath { get; } = Path.Combine(DataDirectory, "mapped-drives.json");

    /// <summary>%LOCALAPPDATA%\UltimateRDP\staging, local copies offered to drags in the session.</summary>
    public static string StagingDirectory { get; } = Path.Combine(DataDirectory, "staging");

    /// <summary>The claims of the agents of the user on the Send To shortcut, one file per session, since 0.9.0.</summary>
    public static string SendToClaimsDirectory { get; } = Path.Combine(DataDirectory, "sendto-claims");

    /// <summary>%LOCALAPPDATA%\UltimateRDP\logs</summary>
    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    private static string ReadVersion()
    {
        var assembly = typeof(AgentInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
