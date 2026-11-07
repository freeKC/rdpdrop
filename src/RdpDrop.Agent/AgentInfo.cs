using System.Reflection;

namespace RdpDrop.Agent;

/// <summary>
/// Fixed facts about the agent: version, names and file locations.
/// </summary>
internal static class AgentInfo
{
    public const string ProductName = "RdpDrop agent";

    public const string ProjectUrl = "https://github.com/freeKC/rdpdrop";

    /// <summary>Per session mutex that keeps a single agent in each user session.</summary>
    public const string InstanceMutexName = @"Local\RdpDrop.Agent";

    /// <summary>Per session event that asks the running agent to exit.</summary>
    public const string ExitEventName = @"Local\RdpDrop.Agent.Exit";

    /// <summary>Name of the value written under the Run key.</summary>
    public const string RunValueName = "RdpDrop";

    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Version without the source revision suffix, for example "0.2.1".</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>%LOCALAPPDATA%\RdpDrop</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RdpDrop");

    /// <summary>%LOCALAPPDATA%\RdpDrop\agent.json</summary>
    public static string SettingsPath { get; } = Path.Combine(DataDirectory, "agent.json");

    /// <summary>%LOCALAPPDATA%\RdpDrop\staging, local copies offered to drags in the session.</summary>
    public static string StagingDirectory { get; } = Path.Combine(DataDirectory, "staging");

    /// <summary>%LOCALAPPDATA%\RdpDrop\logs</summary>
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
