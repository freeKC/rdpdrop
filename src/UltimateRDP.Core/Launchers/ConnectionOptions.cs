using System.Globalization;
using System.Text;
using UltimateRDP.Core.RdpFiles;

namespace UltimateRDP.Core.Launchers;

/// <summary>Where the Windows key combinations go: the values of <c>keyboardhook</c>.</summary>
public enum WindowsKeysTarget
{
    /// <summary><c>local</c>, <c>keyboardhook:i:0</c>: to this computer.</summary>
    Local,

    /// <summary><c>remote</c>, <c>keyboardhook:i:1</c>: to the remote computer.</summary>
    Remote,

    /// <summary><c>fullscreen</c>, <c>keyboardhook:i:2</c>: to the remote computer in full screen only.</summary>
    FullScreen,
}

/// <summary>Where the sound plays: the values of <c>audiomode</c>.</summary>
public enum AudioTarget
{
    /// <summary><c>local</c>, <c>audiomode:i:0</c>: on this computer.</summary>
    Local,

    /// <summary><c>remote</c>, <c>audiomode:i:1</c>: on the remote computer.</summary>
    Remote,

    /// <summary><c>off</c>, <c>audiomode:i:2</c>: nowhere.</summary>
    Off,
}

/// <summary>
/// A connection given on the command line instead of, or on top of, a .rdp file, since 0.10.0.
/// Each value is null when it is not given; every value maps to keys of a .rdp file
/// (<see cref="ToRdpLines"/>), so that a connection from the command line goes through the
/// same parser and the same code as a file. There is no password, on purpose.
/// </summary>
public sealed record ConnectionOptions
{
    /// <summary>No option given.</summary>
    public static ConnectionOptions None { get; } = new();

    /// <summary><c>--server</c>: host name or IP address, without a port.</summary>
    public string? Server { get; init; }

    /// <summary><c>--port</c>.</summary>
    public int? Port { get; init; }

    /// <summary><c>--user</c>.</summary>
    public string? User { get; init; }

    /// <summary><c>--domain</c>.</summary>
    public string? Domain { get; init; }

    /// <summary><c>--remoteapp</c>: an alias (<c>||Name</c> or <c>Name</c>) or a path on the server, see <see cref="LaunchValues.IsProgramPath"/>.</summary>
    public string? RemoteApp { get; init; }

    /// <summary><c>--remoteapp-name</c>.</summary>
    public string? RemoteAppName { get; init; }

    /// <summary><c>--remoteapp-args</c>.</summary>
    public string? RemoteAppArgs { get; init; }

    /// <summary><c>--gateway</c>: host name or IP address of a Remote Desktop Gateway.</summary>
    public string? Gateway { get; init; }

    /// <summary><c>--multimon</c>: <c>use multimon</c>.</summary>
    public bool? Multimon { get; init; }

    /// <summary><c>--scale</c>: <c>desktopscalefactor</c>, in percent.</summary>
    public int? Scale { get; init; }

    /// <summary><c>--windows-keys</c>: <c>keyboardhook</c>.</summary>
    public WindowsKeysTarget? WindowsKeys { get; init; }

    /// <summary><c>--clipboard</c>: <c>redirectclipboard</c>.</summary>
    public bool? Clipboard { get; init; }

    /// <summary><c>--drives</c>: <c>redirectdrives</c> and <c>drivestoredirect</c> (every drive, or none).</summary>
    public bool? Drives { get; init; }

    /// <summary><c>--printers</c>: <c>redirectprinters</c>.</summary>
    public bool? Printers { get; init; }

    /// <summary><c>--audio</c>: <c>audiomode</c>.</summary>
    public AudioTarget? Audio { get; init; }

    /// <summary>True when no option is given.</summary>
    public bool IsEmpty => Equals(None);

    /// <summary>
    /// The first value that does not follow its rule, or a missing <c>--server</c> or
    /// <c>--remoteapp</c> when <paramref name="hasFile"/> is false; null when the options can be
    /// used.
    /// </summary>
    public LaunchError? Check(bool hasFile)
    {
        if (Invalid(LaunchOptionNames.Server, Server, LaunchValues.IsHost, LaunchValueRule.Host) is { } server)
        {
            return server;
        }

        if (Port is int port && !LaunchValues.IsPort(port))
        {
            return Error(LaunchOptionNames.Port, port.ToString(CultureInfo.InvariantCulture), LaunchValueRule.Port);
        }

        var texts = new (string Option, string? Value, Func<string?, bool> Rule, LaunchValueRule Name)[]
        {
            (LaunchOptionNames.User, User, LaunchValues.IsName, LaunchValueRule.Name),
            (LaunchOptionNames.Domain, Domain, LaunchValues.IsName, LaunchValueRule.Name),
            (LaunchOptionNames.RemoteApp, RemoteApp, LaunchValues.IsProgram, LaunchValueRule.Program),
            (LaunchOptionNames.RemoteAppName, RemoteAppName, LaunchValues.IsName, LaunchValueRule.Name),
            (LaunchOptionNames.RemoteAppArgs, RemoteAppArgs, LaunchValues.IsArguments, LaunchValueRule.Arguments),
            (LaunchOptionNames.Gateway, Gateway, LaunchValues.IsHost, LaunchValueRule.Host),
        };
        foreach (var (option, value, rule, name) in texts)
        {
            if (Invalid(option, value, rule, name) is { } error)
            {
                return error;
            }
        }

        if (Scale is int scale && !LaunchValues.IsScale(scale))
        {
            return Error(LaunchOptionNames.Scale, scale.ToString(CultureInfo.InvariantCulture), LaunchValueRule.Scale);
        }

        if (WindowsKeys is { } keys && !Enum.IsDefined(keys))
        {
            return Error(LaunchOptionNames.WindowsKeys, keys.ToString(), LaunchValueRule.WindowsKeys);
        }

        if (Audio is { } audio && !Enum.IsDefined(audio))
        {
            return Error(LaunchOptionNames.Audio, audio.ToString(), LaunchValueRule.Audio);
        }

        if (!hasFile && !IsEmpty && Server is null)
        {
            return new LaunchError(LaunchErrorKind.NoServer, LaunchOptionNames.Server);
        }

        if (!hasFile && RemoteApp is null && (RemoteAppName is not null || RemoteAppArgs is not null))
        {
            return new LaunchError(LaunchErrorKind.NoProgram, RemoteAppName is not null ? LaunchOptionNames.RemoteAppName : LaunchOptionNames.RemoteAppArgs);
        }

        return null;
    }

    /// <summary>
    /// The lines of a .rdp file that give these options, in the form <c>key:type:value</c>.
    /// <paramref name="baseFile"/> is the file the options go on top of, or null: a
    /// <c>--port</c> without <c>--server</c> keeps the host of the file. With
    /// <c>--server</c>, the address of the file is replaced and the port is <c>--port</c> or
    /// 3389. Every value has been checked (<see cref="Check"/>), so none holds a line break.
    /// </summary>
    public IReadOnlyList<string> ToRdpLines(RdpFile? baseFile)
    {
        var lines = new List<string>();
        if (Server is not null || Port is not null)
        {
            var host = Server ?? RdpAddress.Split(baseFile?.FullAddress ?? string.Empty).Host;
            lines.Add(Line(RdpKeys.FullAddress, host));
            lines.Add(Line(RdpKeys.ServerPort, Port ?? LaunchValues.DefaultPort));
        }

        AddText(lines, RdpKeys.UserName, User);
        AddText(lines, RdpKeys.Domain, Domain);
        if (RemoteApp is { } program)
        {
            lines.Add(Line(RdpKeys.RemoteApplicationMode, 1));
            lines.Add(Line(RdpKeys.RemoteApplicationProgram, LaunchValues.RdpProgram(program)));
        }

        AddText(lines, RdpKeys.RemoteApplicationName, RemoteAppName);
        AddText(lines, RdpKeys.RemoteApplicationCmdLine, RemoteAppArgs);
        if (Gateway is { } gateway)
        {
            lines.Add(Line(RdpKeys.GatewayHostname, gateway));
            lines.Add(Line(RdpKeys.GatewayUsageMethod, 1));
            lines.Add(Line(RdpKeys.GatewayProfileUsageMethod, 1));
        }

        AddSwitch(lines, RdpKeys.UseMultimon, Multimon);
        if (Scale is int scale)
        {
            lines.Add(Line(RdpKeys.DesktopScaleFactor, scale));
        }

        if (WindowsKeys is { } keys)
        {
            lines.Add(Line(RdpKeys.KeyboardHook, (int)keys));
        }

        AddSwitch(lines, RdpKeys.RedirectClipboard, Clipboard);
        if (Drives is bool drives)
        {
            lines.Add(Line(RdpKeys.RedirectDrives, drives ? 1 : 0));
            lines.Add(Line(RdpKeys.DrivesToRedirect, drives ? "*" : string.Empty));
        }

        AddSwitch(lines, RdpKeys.RedirectPrinters, Printers);
        if (Audio is { } audio)
        {
            lines.Add(Line(RdpKeys.AudioMode, (int)audio));
        }

        return lines;
    }

    /// <summary>
    /// The text of a .rdp file: <paramref name="baseContent"/> (the text of a file, or null)
    /// followed by <see cref="ToRdpLines"/>. The parser keeps the last line of a key, so the
    /// options win over the file.
    /// </summary>
    public string ToRdpContent(string? baseContent)
    {
        var baseFile = baseContent is null ? null : RdpFile.Parse(baseContent);
        var text = new StringBuilder();
        if (!string.IsNullOrEmpty(baseContent))
        {
            text.Append(baseContent);
            if (!baseContent.EndsWith('\n'))
            {
                text.Append("\r\n");
            }
        }

        foreach (var line in ToRdpLines(baseFile))
        {
            text.Append(line).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// The options that give the connection of <paramref name="file"/>, for the launcher
    /// generator: its address, account, RemoteApp program and gateway, and the display and
    /// redirection keys it sets. A key the file does not set stays null. A list of drives is
    /// read as every drive, a RemoteApp file without a program as a desktop.
    /// </summary>
    public static ConnectionOptions FromRdpFile(RdpFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var (host, addressPort) = RdpAddress.Split(file.FullAddress ?? string.Empty);
        var remoteApp = RemoteAppLaunch.FromFile(file, out _);
        var gateway = file.GatewayHostname?.Trim() is { Length: > 0 } gatewayHost && file.GatewayUsageMethod is 1 or 2 ? gatewayHost : null;
        return new ConnectionOptions
        {
            Server = NonBlank(host),
            Port = addressPort ?? file.ServerPort,
            User = NonBlank(file.UserName),
            Domain = NonBlank(file.Domain),
            RemoteApp = remoteApp?.Program,
            RemoteAppName = remoteApp?.Program is null ? null : remoteApp.Name,
            RemoteAppArgs = remoteApp?.Program is null ? null : remoteApp.Args,
            Gateway = gateway,
            Multimon = file.UseMultimon,
            Scale = file.DesktopScaleFactor,
            WindowsKeys = file.KeyboardHook is int hook ? (WindowsKeysTarget)hook : null,
            Clipboard = file.GetInt(RdpKeys.RedirectClipboard) is null ? null : file.RedirectClipboard,
            Drives = file.GetInt(RdpKeys.RedirectDrives) is null && file.GetEntry(RdpKeys.DrivesToRedirect) is null ? null : file.RequestsDriveRedirection,
            Printers = file.GetInt(RdpKeys.RedirectPrinters) is null ? null : file.RedirectPrinters,
            Audio = file.GetInt(RdpKeys.AudioMode) is int mode and >= 0 and <= 2 ? (AudioTarget)mode : null,
        };
    }

    private static string? NonBlank(string? value) => value?.Trim() is { Length: > 0 } text ? text : null;

    private static LaunchError? Invalid(string option, string? value, Func<string?, bool> rule, LaunchValueRule name) =>
        value is not null && !rule(value) ? Error(option, value, name) : null;

    private static LaunchError Error(string option, string value, LaunchValueRule rule) => new(LaunchErrorKind.InvalidValue, option, value, rule);

    private static void AddText(List<string> lines, string key, string? value)
    {
        if (value is not null)
        {
            lines.Add(Line(key, value));
        }
    }

    private static void AddSwitch(List<string> lines, string key, bool? value)
    {
        if (value is bool on)
        {
            lines.Add(Line(key, on ? 1 : 0));
        }
    }

    private static string Line(string key, string value) => $"{key}:s:{value}";

    private static string Line(string key, int value) => string.Create(CultureInfo.InvariantCulture, $"{key}:i:{value}");
}
