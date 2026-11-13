using System.Reflection;

namespace UltimateRDP.Client;

internal static class AppInfo
{
    public const string Name = Core.Brand.Product;

    /// <summary>The brand, "UltimateRDP by Aprogsys", where the name stands alone as a brand; see <see cref="Core.Brand"/>.</summary>
    public const string Brand = Core.Brand.Text;

    public const string ProjectUrl = "https://github.com/freeKC/ultimaterdp";

    public const string Publisher = "APROGSYS";

    public const string PublisherUrl = "https://www.aprogsys.com/";

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
