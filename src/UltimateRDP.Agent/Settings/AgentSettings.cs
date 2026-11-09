using System.Text.Json;
using System.Text.Json.Serialization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Core.Agent;

namespace UltimateRDP.Agent.Settings;

/// <summary>
/// Settings stored in %LOCALAPPDATA%\UltimateRDP\agent.json. Loading never fails: a missing
/// or unreadable file gives the defaults. Thread safe: the values are read by drag
/// threads and changed from the tray menu.
/// </summary>
internal sealed class AgentSettings
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _path;
    private readonly FileLogger _logger;
    private readonly Lock _saveGate = new();
    private volatile bool _stageFiles;

    private AgentSettings(string path, FileLogger logger, LogLevel logLevel, bool stageFiles, TrayIconMode trayIcon)
    {
        _path = path;
        _logger = logger;
        LogLevel = logLevel;
        _stageFiles = stageFiles;
        TrayIcon = trayIcon;
    }

    /// <summary>
    /// "Stage files in the session before the drop": the drag inside the session offers
    /// local copies instead of the redirected drive paths. Off by default.
    /// </summary>
    public bool StageFiles => _stageFiles;

    /// <summary>Lowest level written to the log. Only editable in the file.</summary>
    public LogLevel LogLevel { get; }

    /// <summary>When the tray icon is shown. Auto by default. Only editable in the file.</summary>
    public TrayIconMode TrayIcon { get; }

    /// <summary>True when the settings file exists. False without a usable path.</summary>
    public static bool FileExists(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the file. Never throws and never shows anything: a missing, unreadable or
    /// malformed file gives the defaults and a warning in the log.
    /// </summary>
    public static AgentSettings Load(string path, FileLogger logger)
    {
        var logLevel = LogLevel.Info;
        var stageFiles = false;
        var trayIcon = TrayIconMode.Auto;
        try
        {
            if (FileExists(path))
            {
                var data = JsonSerializer.Deserialize<SettingsData>(WithoutByteOrderMark(File.ReadAllBytes(path)), JsonOptions);
                if (data is not null)
                {
                    // A number out of range would silence the log entirely.
                    logLevel = data.LogLevel is { } level && Enum.IsDefined(level) ? level : LogLevel.Info;
                    stageFiles = data.StageFiles ?? false;
                    trayIcon = TrayIconModes.Parse(data.TrayIcon, out var recognized);
                    if (!recognized)
                    {
                        logger.Warn($"The trayIcon setting \"{data.TrayIcon}\" is unknown; using \"auto\". Use \"auto\", \"always\" or \"never\".");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Could not read the settings file {path}; using the defaults. {ex.Message}");
        }

        return new AgentSettings(path, logger, logLevel, stageFiles, trayIcon);
    }

    /// <summary>
    /// Changes <see cref="StageFiles"/> and saves the file. Returns false when the file
    /// could not be written; the new value is used anyway until the agent exits.
    /// </summary>
    public bool SetStageFiles(bool value)
    {
        _stageFiles = value;
        return Save();
    }

    /// <summary>Readable summary for the log.</summary>
    public override string ToString() =>
        $"stage files {StageFiles}, log level {LogLevel}, tray icon {TrayIconModes.ToSetting(TrayIcon)}";

    private bool Save()
    {
        var data = new SettingsData
        {
            LogLevel = LogLevel == LogLevel.Info ? null : LogLevel,
            StageFiles = StageFiles,
            TrayIcon = TrayIcon == TrayIconMode.Auto ? null : TrayIconModes.ToSetting(TrayIcon),
        };

        lock (_saveGate)
        {
            try
            {
                if (!Path.IsPathFullyQualified(_path))
                {
                    throw new IOException("The folder of the user profile is unknown.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions));
                File.Move(temp, _path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                _logger.Warn($"Could not save the settings file {_path}. {ex.Message}");
                return false;
            }
        }
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
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    /// <summary>Shape of agent.json.</summary>
    private sealed class SettingsData
    {
        /// <summary>"debug", "info", "warn" or "error". Info when absent.</summary>
        public LogLevel? LogLevel { get; set; }

        /// <summary>Offer staged local copies to the drag in the session. False when absent.</summary>
        public bool? StageFiles { get; set; }

        /// <summary>
        /// "auto", "always" or "never". Auto when absent. Read as text, so that an unknown
        /// value only resets this setting and not the whole file.
        /// </summary>
        public string? TrayIcon { get; set; }
    }
}
