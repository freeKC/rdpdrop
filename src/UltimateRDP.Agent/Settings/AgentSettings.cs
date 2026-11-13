using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Core.Agent;

namespace UltimateRDP.Agent.Settings;

/// <summary>
/// Settings stored in %LOCALAPPDATA%\UltimateRDP\agent.json, on top of the machine wide
/// defaults of %ProgramData%\UltimateRDP\agent.json when an administrator wrote that file: each
/// key of the file of the user wins over the same key of the machine file
/// (<see cref="SettingsLayers"/>). Loading never fails: a missing or unreadable file gives the
/// defaults. The tray menu only writes the file of the user, and only the key it changes.
/// Thread safe: the values are read by drag threads and changed from the tray menu.
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
    private volatile MapClientDrivesMode _mapClientDrives;
    private readonly JsonObject _user;

    private AgentSettings(string path, FileLogger logger, JsonObject user, IReadOnlyList<string> machineKeys, LogLevel logLevel, bool stageFiles, TrayIconMode trayIcon, FastTransferMode fastTransfer, long fastTransferMinBytes, ChannelReadMode channelRead, DirectTransferMode directTransfer, (int First, int Last) directPortRange, int directConnections, int directMaxMegabytesPerSecond, MapClientDrivesMode mapClientDrives)
    {
        _path = path;
        _logger = logger;
        _user = user;
        MachineKeys = machineKeys;
        _mapClientDrives = mapClientDrives;
        LogLevel = logLevel;
        _stageFiles = stageFiles;
        TrayIcon = trayIcon;
        _fastTransfer = fastTransfer;
        FastTransferMinBytes = fastTransferMinBytes;
        ChannelRead = channelRead;
        _directTransfer = directTransfer;
        DirectPortRange = directPortRange;
        DirectConnections = directConnections;
        DirectMaxMegabytesPerSecond = directMaxMegabytesPerSecond;
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

    /// <summary>
    /// The most connections a direct transfer may use, 1 to 16, 8 by default; the client uses
    /// fewer for a small transfer. Only editable in the file; read at startup.
    /// </summary>
    public int DirectConnections { get; }

    /// <summary>
    /// A limit on the rate of a direct transfer, all its connections together, in millions of
    /// bytes per second, to protect the server; 0, the default, for no limit. Only editable in
    /// the file; read at startup.
    /// </summary>
    public int DirectMaxMegabytesPerSecond { get; }

    /// <summary>
    /// "Client drives on drive letters": whether the redirected drives of the client get
    /// letters of the session while an UltimateRDP client is connected. Never by default.
    /// </summary>
    public MapClientDrivesMode MapClientDrives => _mapClientDrives;

    /// <summary>The keys that come from the machine file, because the file of the user does not set them.</summary>
    public IReadOnlyList<string> MachineKeys { get; }

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
    /// Reads the machine file at <paramref name="machinePath"/>, when it exists, then the file
    /// of the user at <paramref name="path"/>, and merges them key by key. Never throws and
    /// never shows anything: a missing, unreadable or malformed file is left out, with a
    /// warning in the log for one that exists.
    /// </summary>
    public static AgentSettings Load(string path, FileLogger logger, string? machinePath = null)
    {
        var user = ReadObject(path, logger);
        var machine = ReadObject(machinePath ?? AgentInfo.MachineSettingsPath, logger);
        var machineKeys = SettingsLayers.MachineKeysInForce(machine, user);
        var merged = SettingsLayers.Merge(machine, user);
        var mapClientDrives = MapClientDrivesMode.Never;
        var logLevel = LogLevel.Info;
        var stageFiles = false;
        var trayIcon = TrayIconMode.Auto;
        var fastTransfer = FastTransferMode.Auto;
        var fastTransferMinBytes = FastTransferPolicy.DefaultMinBytes;
        var channelRead = ChannelReadMode.Wts;
        var directTransfer = DirectTransferMode.Auto;
        (int First, int Last) directPortRange = (DirectTransferPolicy.DefaultFirstPort, DirectTransferPolicy.DefaultLastPort);
        var directConnections = DirectTransferPolicy.DefaultConnections;
        var directMaxMegabytesPerSecond = 0;
        try
        {
            {
                var data = merged.Deserialize<SettingsData>(JsonOptions);
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

                    directConnections = DirectTransferPolicy.ParseConnections(data.DirectConnections, out recognized);
                    if (!recognized)
                    {
                        logger.Warn($"The directConnections setting {data.DirectConnections} is not from 1 to {DirectTransferPolicy.MaxConnections}; using {DirectTransferPolicy.DefaultConnections}.");
                    }

                    directMaxMegabytesPerSecond = DirectTransferPolicy.ParseMaxMegabytesPerSecond(data.DirectMaxMegabytesPerSecond, out recognized);
                    if (!recognized)
                    {
                        logger.Warn($"The directMaxMegabytesPerSecond setting {data.DirectMaxMegabytesPerSecond} is not from 0 to {DirectTransferPolicy.MaxMegabytesPerSecond}; using 0, no limit.");
                    }

                    mapClientDrives = ClientDriveLetters.Parse(data.MapClientDrives, out recognized);
                    if (!recognized)
                    {
                        logger.Warn($"The mapClientDrives setting \"{data.MapClientDrives}\" is unknown; using \"never\". Use \"never\" or \"auto\".");
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
            logger.Warn($"Could not read the settings of {path}{(machine is null ? string.Empty : " and " + (machinePath ?? AgentInfo.MachineSettingsPath))}; using the defaults. {ex.Message}");
        }

        return new AgentSettings(path, logger, user ?? new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true }), machineKeys, logLevel, stageFiles, trayIcon, fastTransfer, fastTransferMinBytes, channelRead, directTransfer, directPortRange, directConnections, directMaxMegabytesPerSecond, mapClientDrives);
    }

    /// <summary>
    /// The JSON object of a settings file, or null when the file does not exist, cannot be
    /// read or is not a JSON object; the last two give a warning.
    /// </summary>
    private static JsonObject? ReadObject(string path, FileLogger logger)
    {
        if (!FileExists(path))
        {
            return null;
        }

        try
        {
            var value = SettingsLayers.ParseObject(File.ReadAllBytes(path), out var error);
            if (value is null)
            {
                logger.Warn($"Could not read the settings file {path}; it is left out. {error}");
            }

            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            logger.Warn($"Could not read the settings file {path}; it is left out. {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Changes <see cref="StageFiles"/> and saves the file. Returns false when the file
    /// could not be written; the new value is used anyway until the agent exits.
    /// </summary>
    public bool SetStageFiles(bool value)
    {
        _stageFiles = value;
        return Save("stageFiles", JsonValue.Create(value));
    }

    /// <summary>
    /// Changes <see cref="FastTransfer"/> and saves the file. Returns false when the file
    /// could not be written; the new value is used anyway until the agent exits.
    /// </summary>
    public bool SetFastTransfer(FastTransferMode value)
    {
        _fastTransfer = value;
        return Save("fastTransfer", JsonValue.Create(FastTransferPolicy.ToSetting(value)));
    }

    /// <summary>
    /// Changes <see cref="DirectTransfer"/> and saves the file. Returns false when the file
    /// could not be written; the new value is used anyway until the agent exits.
    /// </summary>
    public bool SetDirectTransfer(DirectTransferMode value)
    {
        _directTransfer = value;
        return Save("directTransfer", JsonValue.Create(DirectTransferPolicy.ToSetting(value)));
    }

    /// <summary>
    /// Changes <see cref="MapClientDrives"/> and saves the file. Returns false when the file
    /// could not be written; the new value is used anyway until the agent exits.
    /// </summary>
    public bool SetMapClientDrives(MapClientDrivesMode value)
    {
        _mapClientDrives = value;
        return Save("mapClientDrives", JsonValue.Create(ClientDriveLetters.ToSetting(value)));
    }

    /// <summary>Readable summary for the log.</summary>
    public override string ToString() =>
        $"stage files {StageFiles}, fast transfer {FastTransferPolicy.ToSetting(FastTransfer)} from {FastTransferMinBytes} bytes, direct connection {DirectTransferPolicy.ToSetting(DirectTransfer)} on ports {DirectTransferPolicy.ToSetting(DirectPortRange)} with up to {DirectConnections} connection(s){(DirectMaxMegabytesPerSecond > 0 ? $" and at most {DirectMaxMegabytesPerSecond} MB/s" : string.Empty)}, log level {LogLevel}, tray icon {TrayIconModes.ToSetting(TrayIcon)}, channel read {ChannelReadModes.ToSetting(ChannelRead)}, client drives on letters {ClientDriveLetters.ToSetting(MapClientDrives)}{(MachineKeys.Count == 0 ? string.Empty : ", from the machine settings: " + string.Join(", ", MachineKeys))}";

    /// <summary>
    /// Writes <paramref name="key"/> into the file of the user, which keeps its other keys as
    /// they are. The machine file is never written.
    /// </summary>
    private bool Save(string key, JsonNode? value)
    {
        lock (_saveGate)
        {
            try
            {
                if (!Path.IsPathFullyQualified(_path))
                {
                    throw new IOException("The folder of the user profile is unknown.");
                }

                SettingsLayers.SetUserValue(_user, key, value);
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, _user.ToJsonString(JsonOptions));
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

        /// <summary>Most connections of a direct transfer, 1 to 16; 8 when absent.</summary>
        public int? DirectConnections { get; set; }

        /// <summary>Limit of a direct transfer in millions of bytes per second; 0 or absent for no limit.</summary>
        public int? DirectMaxMegabytesPerSecond { get; set; }

        /// <summary>"never" or "auto". Never when absent. Read as text, like <see cref="TrayIcon"/>.</summary>
        public string? MapClientDrives { get; set; }
    }
}
