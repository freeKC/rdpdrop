using System.Globalization;

namespace UltimateRDP.Core.RdpFiles;

/// <summary>
/// Settings read from a Remote Desktop <c>.rdp</c> file.
/// </summary>
/// <remarks>
/// Each line has the form <c>key:type:value</c>. The line is split on its first two
/// colons only, so values may contain colons. Keys are case insensitive. Blank lines,
/// lines that start with <c>#</c> or <c>;</c>, lines without two colons and lines with
/// a type other than <c>s</c>, <c>i</c> or <c>b</c> are ignored. When a key appears
/// more than once, the last line wins.
/// </remarks>
public sealed class RdpFile
{
    private readonly Dictionary<string, RdpFileEntry> _entries;

    private RdpFile(Dictionary<string, RdpFileEntry> entries)
    {
        _entries = entries;
    }

    /// <summary>Every setting, in no particular order.</summary>
    public IReadOnlyCollection<RdpFileEntry> Entries => _entries.Values;

    /// <summary><c>full address</c>: host name or address, possibly with <c>:port</c>.</summary>
    public string? FullAddress => GetString(RdpKeys.FullAddress);

    /// <summary><c>server port</c>.</summary>
    public int? ServerPort => GetInt(RdpKeys.ServerPort);

    /// <summary><c>username</c>.</summary>
    public string? UserName => GetString(RdpKeys.UserName);

    /// <summary><c>domain</c>.</summary>
    public string? Domain => GetString(RdpKeys.Domain);

    /// <summary><c>gatewayhostname</c>.</summary>
    public string? GatewayHostname => GetString(RdpKeys.GatewayHostname);

    /// <summary><c>gatewayusagemethod</c>.</summary>
    public int? GatewayUsageMethod => GetInt(RdpKeys.GatewayUsageMethod);

    /// <summary><c>gatewaycredentialssource</c>.</summary>
    public int? GatewayCredentialsSource => GetInt(RdpKeys.GatewayCredentialsSource);

    /// <summary><c>gatewayprofileusagemethod</c>.</summary>
    public int? GatewayProfileUsageMethod => GetInt(RdpKeys.GatewayProfileUsageMethod);

    /// <summary><c>authentication level</c>.</summary>
    public int? AuthenticationLevel => GetInt(RdpKeys.AuthenticationLevel);

    /// <summary><c>prompt for credentials</c>. False when absent.</summary>
    public bool PromptForCredentials => GetBool(RdpKeys.PromptForCredentials, false);

    /// <summary><c>redirectclipboard</c>. True when absent, like mstsc.</summary>
    public bool RedirectClipboard => GetBool(RdpKeys.RedirectClipboard, true);

    /// <summary><c>redirectdrives</c>. False when absent.</summary>
    public bool RedirectDrives => GetBool(RdpKeys.RedirectDrives, false);

    /// <summary><c>drivestoredirect</c>, for example <c>*</c> or <c>C:;D:;</c>.</summary>
    public string? DrivesToRedirect => GetString(RdpKeys.DrivesToRedirect);

    /// <summary>
    /// True when the file asks for drive redirection, either with
    /// <c>redirectdrives:i:1</c> or with a non empty <c>drivestoredirect</c>.
    /// </summary>
    public bool RequestsDriveRedirection => RedirectDrives || !string.IsNullOrWhiteSpace(DrivesToRedirect);

    /// <summary><c>desktopwidth</c>.</summary>
    public int? DesktopWidth => GetInt(RdpKeys.DesktopWidth);

    /// <summary><c>desktopheight</c>.</summary>
    public int? DesktopHeight => GetInt(RdpKeys.DesktopHeight);

    /// <summary><c>screen mode id</c>: 1 for a window, 2 for full screen.</summary>
    public int? ScreenModeId => GetInt(RdpKeys.ScreenModeId);

    /// <summary>
    /// <c>audiomode</c>: 0 plays the sound on this computer, 1 on the remote computer,
    /// 2 nowhere. 0 when absent or out of range, like mstsc.
    /// </summary>
    public int AudioMode => GetInt(RdpKeys.AudioMode) is int mode and >= 0 and <= 2 ? mode : 0;

    /// <summary><c>audiocapturemode</c>: records from this computer. False when absent.</summary>
    public bool AudioCaptureMode => GetBool(RdpKeys.AudioCaptureMode, false);

    /// <summary><c>redirectprinters</c>. True when absent, like mstsc.</summary>
    public bool RedirectPrinters => GetBool(RdpKeys.RedirectPrinters, true);

    /// <summary><c>redirectcomports</c>. False when absent.</summary>
    public bool RedirectComPorts => GetBool(RdpKeys.RedirectComPorts, false);

    /// <summary><c>redirectsmartcards</c>. True when absent, like mstsc.</summary>
    public bool RedirectSmartCards => GetBool(RdpKeys.RedirectSmartCards, true);

    /// <summary>
    /// <c>use multimon</c>: the session uses every monitor in full screen. Null when
    /// absent or not an integer, so that the caller can keep its own choice.
    /// </summary>
    public bool? UseMultimon => GetInt(RdpKeys.UseMultimon) is int value ? value != 0 : null;

    /// <summary>
    /// <c>keyboardhook</c>: where Windows key combinations go. 0 to this computer, 1 to
    /// the remote computer, 2 to the remote computer in full screen only. Null when
    /// absent or out of range.
    /// </summary>
    public int? KeyboardHook => GetInt(RdpKeys.KeyboardHook) is int mode and >= 0 and <= 2 ? mode : null;

    /// <summary>
    /// <c>desktopscalefactor</c>: scale factor of the remote desktop in percent. Null when
    /// absent or outside 100 to 500, the range the Remote Desktop control accepts.
    /// </summary>
    public int? DesktopScaleFactor => GetInt(RdpKeys.DesktopScaleFactor) is int factor and >= 100 and <= 500 ? factor : null;

    /// <summary>
    /// <c>remoteapplicationmode</c>: the connection opens a published program in windows of
    /// its own (RemoteApp) instead of a desktop. False when absent.
    /// </summary>
    public bool RemoteApplicationMode => GetBool(RdpKeys.RemoteApplicationMode, false);

    /// <summary>
    /// <c>remoteapplicationprogram</c>: the alias of a published program, such as
    /// <c>||MyApp</c>, or the path of a program on the server. Null when absent or blank.
    /// </summary>
    public string? RemoteApplicationProgram => NonBlank(RdpKeys.RemoteApplicationProgram);

    /// <summary><c>remoteapplicationname</c>: the name of the program shown to the user. Null when absent or blank.</summary>
    public string? RemoteApplicationName => NonBlank(RdpKeys.RemoteApplicationName);

    /// <summary><c>remoteapplicationcmdline</c>: the arguments of the program. Null when absent or blank.</summary>
    public string? RemoteApplicationCmdLine => NonBlank(RdpKeys.RemoteApplicationCmdLine);

    /// <summary><c>remoteapplicationexpandcmdline</c>: the server expands the variables of the arguments. False when absent.</summary>
    public bool RemoteApplicationExpandCmdLine => GetBool(RdpKeys.RemoteApplicationExpandCmdLine, false);

    /// <summary><c>remoteapplicationexpandworkingdir</c>: the server expands the variables of the working folder. False when absent.</summary>
    public bool RemoteApplicationExpandWorkingDir => GetBool(RdpKeys.RemoteApplicationExpandWorkingDir, false);

    /// <summary><c>remoteapplicationfile</c>: a file on the server that the program opens. Null when absent or blank.</summary>
    public string? RemoteApplicationFile => NonBlank(RdpKeys.RemoteApplicationFile);

    /// <summary>
    /// Reads and parses a file. UTF-16 (with a byte order mark, as written by mstsc) and
    /// UTF-8 (with or without a byte order mark) are supported.
    /// </summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static RdpFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return FromBytes(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Reads the text of a file, with the encodings of <see cref="Load"/>, without parsing it:
    /// options of the command line go on top of it.
    /// </summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static string LoadText(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return RdpFileEncoding.Decode(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Parses the raw bytes of a file, detecting the encoding as <see cref="Load"/> does.
    /// </summary>
    public static RdpFile FromBytes(ReadOnlySpan<byte> bytes) => Parse(RdpFileEncoding.Decode(bytes));

    /// <summary>
    /// Parses the text of a file.
    /// </summary>
    public static RdpFile Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var entries = new Dictionary<string, RdpFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            if (TryParseLine(rawLine) is { } entry)
            {
                entries[entry.Key] = entry;
            }
        }

        return new RdpFile(entries);
    }

    /// <summary>
    /// Returns the setting, or null when the key is absent.
    /// </summary>
    public RdpFileEntry? GetEntry(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _entries.TryGetValue(key.Trim(), out var entry) ? entry : null;
    }

    /// <summary>
    /// Returns the value as written, whatever its type, or null when the key is absent.
    /// </summary>
    public string? GetString(string key) => GetEntry(key)?.Value;

    /// <summary>
    /// Returns the value as an integer, or null when the key is absent or the value is
    /// not an integer.
    /// </summary>
    public int? GetInt(string key)
    {
        var value = GetString(key);
        return int.TryParse(value?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    /// <summary>
    /// Returns true for a non zero integer and false for zero. Returns
    /// <paramref name="defaultValue"/> when the key is absent or not an integer.
    /// </summary>
    public bool GetBool(string key, bool defaultValue) =>
        GetInt(key) is { } number ? number != 0 : defaultValue;

    /// <summary>
    /// Returns the bytes of a hexadecimal value, or null when the key is absent or the
    /// value is not valid hexadecimal.
    /// </summary>
    public byte[]? GetBinary(string key)
    {
        var value = GetString(key)?.Trim();
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private string? NonBlank(string key) => GetString(key)?.Trim() is { Length: > 0 } value ? value : null;

    private static RdpFileEntry? TryParseLine(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line[0] is '#' or ';')
        {
            return null;
        }

        var first = line.IndexOf(':');
        if (first <= 0)
        {
            return null;
        }

        var second = line.IndexOf(':', first + 1);
        if (second < 0)
        {
            return null;
        }

        var key = line[..first].Trim();
        var type = line[(first + 1)..second].Trim();
        if (key.Length == 0 || type.Length != 1)
        {
            return null;
        }

        var typeLetter = char.ToLowerInvariant(type[0]);
        if (typeLetter is not ('s' or 'i' or 'b'))
        {
            return null;
        }

        return new RdpFileEntry(key, typeLetter, line[(second + 1)..]);
    }
}
