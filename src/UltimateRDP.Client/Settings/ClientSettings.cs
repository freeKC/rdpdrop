using System.Text.Json;
using System.Text.Json.Serialization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Core.Agent;
using UltimateRDP.Core.Localization;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Recent;
using UltimateRDP.Core.Transfer;

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

    private const string InfoLevel = "info";
    private const string DebugLevel = "debug";

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

    /// <summary>
    /// Raw bytes per chunk of the fast transfer, from 4 KiB to 256 KiB, 32 KiB by default.
    /// Only editable in the file, so that it can be tuned without a new release.
    /// </summary>
    public int TransferChunkBytes { get; set; } = TransferSender.DefaultChunkSize;

    /// <summary>
    /// Most characters handed to the control in one call, from 50 000 to 1 048 576,
    /// 200 000 by default. Only editable in the file.
    /// </summary>
    public int TransferBatchCharacters { get; set; } = SendQueue.DefaultMaxBatchCharacters;

    /// <summary>
    /// Most bytes of a transfer sent and not yet acknowledged, from 1 MiB to 32 MiB, 4 MiB
    /// by default. A window below the acknowledgement interval of the agent, 2 MiB, plus one
    /// chunk is raised to that. Only editable in the file.
    /// </summary>
    public long TransferWindowBytes { get; set; } = TransferSender.DefaultWindowBytes;

    /// <summary>
    /// <c>auto</c> by default: when the agent offers a direct connection for a fast
    /// transfer, the files go through it, and through the channel when it cannot be used.
    /// <c>never</c> always sends them through the channel. Only editable in the file.
    /// </summary>
    public string DirectTransfer { get; set; } = DirectTransferPolicy.ToSetting(DirectTransferMode.Auto);

    /// <summary>
    /// The most connections of a direct transfer, 1 to 16, 8 by default. The client opens one
    /// connection per started 32 MiB of the transfer, within this setting and the offer of
    /// the agent. Only editable in the file.
    /// </summary>
    public int DirectConnections { get; set; } = DirectTransferPolicy.DefaultConnections;

    /// <summary>The mode of <see cref="DirectTransfer"/>.</summary>
    [JsonIgnore]
    public DirectTransferMode DirectTransferMode => DirectTransferPolicy.Parse(DirectTransfer, out _);

    /// <summary>
    /// <c>info</c> by default, or <c>debug</c> to also log every message sent to the agent
    /// and every call of the control. Only editable in the file.
    /// </summary>
    public string LogLevel { get; set; } = InfoLevel;

    /// <summary>True when <see cref="LogLevel"/> asks for the debug lines.</summary>
    [JsonIgnore]
    public bool DebugLog => string.Equals(LogLevel, DebugLevel, StringComparison.Ordinal);

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
        $"display mode {DisplayMode}, scale factor {ScaleFactor?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto"}, keyboard hook mode {KeyboardHookMode}, all monitors {UseAllMonitors}, language {Language}, transfer chunk {TransferChunkBytes} bytes, transfer window {TransferWindowBytes} bytes, channel call up to {TransferBatchCharacters} characters, direct connection {DirectTransfer} with up to {DirectConnections} connection(s), log level {LogLevel}";

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
        TransferChunkBytes = TransferSender.ClampChunkSize(TransferChunkBytes);
        TransferBatchCharacters = SendQueue.ClampBatchLimit(TransferBatchCharacters);
        TransferWindowBytes = TransferSender.ClampWindowBytes(TransferWindowBytes);
        LogLevel = string.Equals(LogLevel?.Trim(), DebugLevel, StringComparison.OrdinalIgnoreCase) ? DebugLevel : InfoLevel;
        var directTransfer = DirectTransferPolicy.Parse(DirectTransfer, out var recognized);
        if (!recognized)
        {
            ClientLog.Warn($"The directTransfer setting \"{DirectTransfer}\" is unknown; using \"auto\". Use \"auto\" or \"never\".");
        }

        DirectTransfer = DirectTransferPolicy.ToSetting(directTransfer);
        var directConnections = DirectTransferPolicy.ParseConnections(DirectConnections, out recognized);
        if (!recognized)
        {
            ClientLog.Warn($"The directConnections setting {DirectConnections} is not from 1 to {DirectTransferPolicy.MaxConnections}; using {DirectTransferPolicy.DefaultConnections}.");
        }

        DirectConnections = directConnections;
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
