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
    private volatile FastTransferMode _fastTransfer;
    private volatile DirectTransferMode _directTransfer;

    private AgentSettings(string path, FileLogger logger, LogLevel logLevel, bool stageFiles, TrayIconMode trayIcon, FastTransferMode fastTransfer, long fastTransferMinBytes, ChannelReadMode channelRead, DirectTransferMode directTransfer, (int First, int Last) directPortRange)
    {
        _path = path;
        _logger = logger;
        LogLevel = logLevel;
        _stageFiles = stageFiles;
        TrayIcon = trayIcon;
        _fastTransfer = fastTransfer;
        FastTransferMinBytes = fastTransferMinBytes;
        ChannelRead = channelRead;
        _directTransfer = directTransfer;
        DirectPortRange = directPortRange;
    }

    /// <summary>
    /// "Stage files in the session before the drop": the drag inside the session offers
    /// local copies instead of the redirected drive paths. Off by default.
    /// </summary>
    public bool StageFiles => _stageFiles;

    /// <summary>
    /// "Fast transfer of large files": when the dropped files are sent through the channel
    /// before the drop instead of being read through drive redirection. Auto by default.
    /// </summary>
    public FastTransferMode FastTransfer => _fastTransfer;

    /// <summary>
    /// Threshold of <see cref="FastTransferMode.Auto"/>, in bytes. 10 MiB by default. Only
    /// editable in the file.
    /// </summary>
    public long FastTransferMinBytes { get; }

    /// <summary>
    /// "Direct connection for large files": whether a fast transfer offers the direct TLS
    /// connection, with the channel as the fallback. Auto by default.
    /// </summary>
    public DirectTransferMode DirectTransfer => _directTransfer;

    /// <summary>
    /// The TCP ports the listener of a direct transfer may take, the first free one each
    /// time, "3400-3449" by default. Only editable in the file; read at startup.
    /// </summary>
    public (int First, int Last) DirectPortRange { get; }

    /// <summary>Lowest level written to the log. Only editable in the file.</summary>
    public LogLevel LogLevel { get; }

    /// <summary>When the tray icon is shown. Auto by default. Only editable in the file.</summary>
    public TrayIconMode TrayIcon { get; }

    /// <summary>
    /// How the channel is read: with WTSVirtualChannelRead (wts, the default) or through its
    /// file handle (file, an experiment). Only editable in the file; read at startup.
    /// </summary>
    public ChannelReadMode ChannelRead { get; }

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
        var fastTransfer = FastTransferMode.Auto;
        var fastTransferMinBytes = FastTransferPolicy.DefaultMinBytes;
        var channelRead = ChannelReadMode.Wts;
        var directTransfer = DirectTransferMode.Auto;
        (int First, int Last) directPortRange = (DirectTransferPolicy.DefaultFirstPort, DirectTransferPolicy.DefaultLastPort);
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

                    fastTransfer = FastTransferPolicy.Parse(data.FastTransfer, out recognized);
                    if (!recognized)
                    {
                        logger.Warn($"The fastTransfer setting \"{data.FastTransfer}\" is unknown; using \"auto\". Use \"auto\", \"always\" or \"never\".");
                    }

                    channelRead = ChannelReadModes.Parse(data.ChannelRead, out recognized);
                    if (!recognized)
                    {
                        logger.Warn($"The channelRead setting \"{data.ChannelRead}\" is unknown; using \"wts\". Use \"wts\" or \"file\".");
                    }

                    directTransfer = DirectTransferPolicy.Parse(data.DirectTransfer, out recognized);
                    if (!recognized)
                    {
                        logger.Warn($"The directTransfer setting \"{data.DirectTransfer}\" is unknown; using \"auto\". Use \"auto\" or \"never\".");
                    }

                    directPortRange = DirectTransferPolicy.ParsePortRange(data.DirectPortRange, out recognized);
                    if (!recognized)
                    {
                        logger.Warn($"The directPortRange setting \"{data.DirectPortRange}\" is not a range of at most {DirectTransferPolicy.MaxPorts} ports from {DirectTransferPolicy.MinPort} to 65535; using \"{DirectTransferPolicy.DefaultPortRange}\".");
                    }

                    if (data.FastTransferMinBytes is { } minBytes)
                    {
                        if (minBytes >= 0)
                        {
                            fastTransferMinBytes = minBytes;
                        }
                        else
                        {
                            logger.Warn($"The fastTransferMinBytes setting {minBytes} is negative; using {FastTransferPolicy.DefaultMinBytes}.");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Could not read the settings file {path}; using the defaults. {ex.Message}");
        }

        return new AgentSettings(path, logger, logLevel, stageFiles, trayIcon, fastTransfer, fastTransferMinBytes, channelRead, directTransfer, directPortRange);
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

    /// <summary>
    /// Changes <see cref="FastTransfer"/> and saves the file. Returns false when the file
    /// could not be written; the new value is used anyway until the agent exits.
    /// </summary>
    public bool SetFastTransfer(FastTransferMode value)
    {
        _fastTransfer = value;
        return Save();
    }

    /// <summary>
    /// Changes <see cref="DirectTransfer"/> and saves the file. Returns false when the file
    /// could not be written; the new value is used anyway until the agent exits.
    /// </summary>
    public bool SetDirectTransfer(DirectTransferMode value)
    {
        _directTransfer = value;
        return Save();
    }

    /// <summary>Readable summary for the log.</summary>
    public override string ToString() =>
        $"stage files {StageFiles}, fast transfer {FastTransferPolicy.ToSetting(FastTransfer)} from {FastTransferMinBytes} bytes, direct connection {DirectTransferPolicy.ToSetting(DirectTransfer)} on ports {DirectTransferPolicy.ToSetting(DirectPortRange)}, log level {LogLevel}, tray icon {TrayIconModes.ToSetting(TrayIcon)}, channel read {ChannelReadModes.ToSetting(ChannelRead)}";

    private bool Save()
    {
        var data = new SettingsData
        {
            LogLevel = LogLevel == LogLevel.Info ? null : LogLevel,
            StageFiles = StageFiles,
            TrayIcon = TrayIcon == TrayIconMode.Auto ? null : TrayIconModes.ToSetting(TrayIcon),
            FastTransfer = FastTransfer == FastTransferMode.Auto ? null : FastTransferPolicy.ToSetting(FastTransfer),
            FastTransferMinBytes = FastTransferMinBytes == FastTransferPolicy.DefaultMinBytes ? null : FastTransferMinBytes,
            ChannelRead = ChannelRead == ChannelReadMode.Wts ? null : ChannelReadModes.ToSetting(ChannelRead),
            DirectTransfer = DirectTransfer == DirectTransferMode.Auto ? null : DirectTransferPolicy.ToSetting(DirectTransfer),
            DirectPortRange = DirectPortRange == (DirectTransferPolicy.DefaultFirstPort, DirectTransferPolicy.DefaultLastPort) ? null : DirectTransferPolicy.ToSetting(DirectPortRange),
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

        /// <summary>"auto", "always" or "never". Auto when absent. Read as text, like <see cref="TrayIcon"/>.</summary>
        public string? FastTransfer { get; set; }

        /// <summary>Threshold of the auto mode, in bytes. 10485760 when absent.</summary>
        public long? FastTransferMinBytes { get; set; }

        /// <summary>"wts" or "file". Wts when absent. Read as text, like <see cref="TrayIcon"/>.</summary>
        public string? ChannelRead { get; set; }

        /// <summary>"auto" or "never". Auto when absent. Read as text, like <see cref="TrayIcon"/>.</summary>
        public string? DirectTransfer { get; set; }

        /// <summary>Ports of the direct connection, such as "3400-3449", the default when absent.</summary>
        public string? DirectPortRange { get; set; }
    }
}
