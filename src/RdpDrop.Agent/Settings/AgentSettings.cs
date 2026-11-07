using System.Text.Json;
using System.Text.Json.Serialization;
using RdpDrop.Agent.Logging;

namespace RdpDrop.Agent.Settings;

/// <summary>
/// Settings stored in %LOCALAPPDATA%\RdpDrop\agent.json. Loading never fails: a missing
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

    private AgentSettings(string path, FileLogger logger, LogLevel logLevel, bool stageFiles)
    {
        _path = path;
        _logger = logger;
        LogLevel = logLevel;
        _stageFiles = stageFiles;
    }

    /// <summary>
    /// "Stage files in the session before the drop": the drag inside the session offers
    /// local copies instead of the redirected drive paths. Off by default.
    /// </summary>
    public bool StageFiles => _stageFiles;

    /// <summary>Lowest level written to the log. Only editable in the file.</summary>
    public LogLevel LogLevel { get; }

    public static AgentSettings Load(string path, FileLogger logger)
    {
        var logLevel = LogLevel.Info;
        var stageFiles = false;
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllBytes(path), JsonOptions);
                if (data is not null)
                {
                    // A number out of range would silence the log entirely.
                    logLevel = data.LogLevel is { } level && Enum.IsDefined(level) ? level : LogLevel.Info;
                    stageFiles = data.StageFiles ?? false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            logger.Warn($"Could not read the settings file {path}; using the defaults. {ex.Message}");
        }

        return new AgentSettings(path, logger, logLevel, stageFiles);
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
        $"stage files {StageFiles}, log level {LogLevel}";

    private bool Save()
    {
        var data = new SettingsData
        {
            LogLevel = LogLevel == LogLevel.Info ? null : LogLevel,
            StageFiles = StageFiles,
        };

        lock (_saveGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions));
                File.Move(temp, _path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warn($"Could not save the settings file {_path}. {ex.Message}");
                return false;
            }
        }
    }

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
    }
}
