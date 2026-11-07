using System.Reflection;

namespace RdpDrop.Client;

internal static class AppInfo
{
    public const string Name = "RdpDrop";

    public const string ProjectUrl = "https://github.com/freeKC/rdpdrop";

    /// <summary>Version without the build metadata, for example 0.2.1.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
