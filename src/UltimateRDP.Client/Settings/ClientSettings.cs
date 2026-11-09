using System.Text.Json;
using System.Text.Json.Serialization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Core.Localization;
using UltimateRDP.Core.Recent;

namespace UltimateRDP.Client.Settings;

/// <summary>
/// Display choices, language and recent connections remembered between runs in
/// %LOCALAPPDATA%\UltimateRDP\client.json.
/// Loading never fails: a missing, unreadable or odd file gives the defaults.
/// </summary>
internal sealed class ClientSettings
{
    /// <summary>Keyboard hook mode of mstsc: Windows keys go to the session in full screen only.</summary>
    public const int DefaultKeyboardHookMode = 2;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>Path of the settings file.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UltimateRDP",
        "client.json");

    public DisplayMode DisplayMode { get; set; } = DisplayMode.ResizeSession;

    /// <summary>Desktop scale factor in percent, or null to follow the monitor.</summary>
    public int? ScaleFactor { get; set; }

    /// <summary>0: Windows keys stay on this computer, 1: in the session, 2: in the session in full screen.</summary>
    public int KeyboardHookMode { get; set; } = DefaultKeyboardHookMode;

    public bool UseAllMonitors { get; set; }

    /// <summary>
    /// Language of the user interface: "auto" for the Windows display language, or one of
    /// the two letter codes of <see cref="UiLanguages.Codes"/>.
    /// </summary>
    public string Language { get; set; } = UiLanguages.Automatic;

    /// <summary>Full paths of the .rdp files opened lately, most recent first.</summary>
    public List<string> RecentFiles { get; set; } = [];

    public static ClientSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<ClientSettings>(WithoutByteOrderMark(File.ReadAllBytes(path)), JsonOptions);
                if (loaded is not null)
                {
                    return loaded.Normalized();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            ClientLog.Warn($"Could not read the settings file {path}; using the defaults. {ex.Message}");
        }

        return new ClientSettings();
    }

    /// <summary>
    /// Writes the settings. A failure is logged and otherwise ignored: the choices stay
    /// in use until the client exits.
    /// </summary>
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ClientLog.Warn($"Could not save the settings file {path}. {ex.Message}");
        }
    }

    public override string ToString() =>
        $"display mode {DisplayMode}, scale factor {ScaleFactor?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto"}, keyboard hook mode {KeyboardHookMode}, all monitors {UseAllMonitors}, language {Language}";

    private ClientSettings Normalized()
    {
        if (!Enum.IsDefined(DisplayMode))
        {
            DisplayMode = DisplayMode.ResizeSession;
        }

        if (ScaleFactor is not (>= 100 and <= 500))
        {
            ScaleFactor = null;
        }

        if (KeyboardHookMode is < 0 or > 2)
        {
            KeyboardHookMode = DefaultKeyboardHookMode;
        }

        Language = UiLanguages.NormalizeChoice(Language);
        RecentFiles = [.. RecentList.Normalize(RecentFiles)];
        return this;
    }

    /// <summary>
    /// A file saved by hand, with Notepad for example, may start with the UTF-8 byte order
    /// mark, which the JSON reader refuses.
    /// </summary>
    private static ReadOnlySpan<byte> WithoutByteOrderMark(byte[] bytes) =>
        bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes.AsSpan(3) : bytes;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
