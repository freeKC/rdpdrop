using System.Text.Json;
using System.Text.Json.Serialization;
using RdpDrop.Agent.Logging;
using RdpDrop.Core.Targets;

namespace RdpDrop.Agent.Settings;

/// <summary>
/// Settings stored in %LOCALAPPDATA%\RdpDrop\agent.json. Loading never fails: a missing
/// or unreadable file gives the defaults. Thread safe: the values are read by drop and
/// channel threads and changed from the tray menu.
/// </summary>
internal sealed class AgentSettings
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _path;
    private readonly FileLogger _logger;
    private readonly Lock _saveGate = new();
    private FallbackFolder _fallback;
    private volatile bool _stageFiles;
    private volatile bool _fallbackCopy;

    private AgentSettings(string path, FileLogger logger, FallbackFolder fallback, LogLevel logLevel, bool stageFiles, bool fallbackCopy)
    {
        _path = path;
        _logger = logger;
        _fallback = fallback;
        LogLevel = logLevel;
        _stageFiles = stageFiles;
        _fallbackCopy = fallbackCopy;
    }

    /// <summary>Folder used when the cursor is not over a usable folder.</summary>
    public FallbackFolder Fallback => Volatile.Read(ref _fallback);

    /// <summary>
    /// "Stage files in the session before the drop": the drag inside the session offers
    /// local copies instead of the redirected drive paths. Off by default.
    /// </summary>
    public bool StageFiles => _stageFiles;

    /// <summary>
    /// "Copy to the fallback folder when nothing accepts the drop": after a drag inside the
    /// session that no window accepted, the agent does the 0.1 copy. On by default.
    /// </summary>
    public bool FallbackCopy => _fallbackCopy;

    /// <summary>Lowest level written to the log. Only editable in the file.</summary>
    public LogLevel LogLevel { get; }

    public static AgentSettings Load(string path, FileLogger logger)
    {
        var fallback = FallbackFolder.Default;
        var logLevel = LogLevel.Info;
        var stageFiles = false;
        var fallbackCopy = true;
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllBytes(path), JsonOptions);
                if (data is not null)
                {
                    fallback = ToFallback(data);
                    // A number out of range would silence the log entirely.
                    logLevel = data.LogLevel is { } level && Enum.IsDefined(level) ? level : LogLevel.Info;
                    stageFiles = data.StageFiles ?? false;
                    fallbackCopy = data.FallbackCopy ?? true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            logger.Warn($"Could not read the settings file {path}; using the defaults. {ex.Message}");
        }

        return new AgentSettings(path, logger, fallback, logLevel, stageFiles, fallbackCopy);
    }

    /// <summary>
    /// Changes the fallback folder and saves the file. Returns false when the file
    /// could not be written; the new value is used anyway until the agent exits.
    /// </summary>
    public bool SetFallback(FallbackFolder fallback)
    {
        Volatile.Write(ref _fallback, fallback);
        return Save();
    }

    /// <summary>Changes <see cref="StageFiles"/> and saves the file.</summary>
    public bool SetStageFiles(bool value)
    {
        _stageFiles = value;
        return Save();
    }

    /// <summary>Changes <see cref="FallbackCopy"/> and saves the file.</summary>
    public bool SetFallbackCopy(bool value)
    {
        _fallbackCopy = value;
        return Save();
    }

    /// <summary>Readable summary for the log.</summary>
    public override string ToString() =>
        $"fallback {Fallback.Kind}{(Fallback.CustomPath is null ? string.Empty : " " + Fallback.CustomPath)}, stage files {StageFiles}, fallback copy {FallbackCopy}, log level {LogLevel}";

    private bool Save()
    {
        var fallback = Fallback;
        var data = new SettingsData
        {
            Fallback = fallback.Kind,
            CustomFolder = fallback.Kind == FallbackKind.Custom ? fallback.CustomPath : null,
            LogLevel = LogLevel == LogLevel.Info ? null : LogLevel,
            StageFiles = StageFiles,
            FallbackCopy = FallbackCopy,
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

    private static FallbackFolder ToFallback(SettingsData data) => data.Fallback switch
    {
        FallbackKind.Downloads => new FallbackFolder(FallbackKind.Downloads),
        FallbackKind.Custom when !string.IsNullOrWhiteSpace(data.CustomFolder) => new FallbackFolder(FallbackKind.Custom, data.CustomFolder),
        _ => FallbackFolder.Default,
    };

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
        /// <summary>"desktop", "downloads" or "custom".</summary>
        public FallbackKind Fallback { get; set; } = FallbackKind.Desktop;

        /// <summary>Folder used when <see cref="Fallback"/> is "custom".</summary>
        public string? CustomFolder { get; set; }

        /// <summary>"debug", "info", "warn" or "error". Info when absent.</summary>
        public LogLevel? LogLevel { get; set; }

        /// <summary>Offer staged local copies to the drag in the session. False when absent.</summary>
        public bool? StageFiles { get; set; }

        /// <summary>Copy to the fallback folder when nothing accepts a drop. True when absent.</summary>
        public bool? FallbackCopy { get; set; }
    }
}
