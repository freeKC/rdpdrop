using System.Globalization;
using System.Runtime.InteropServices;
using MSTSCLib;
using UltimateRDP.Client.Logging;
using UltimateRDP.Core.Display;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.RdpFiles;

namespace UltimateRDP.Client.Rdp;

/// <summary>
/// Applies the settings of a .rdp file to the control before a connection.
/// </summary>
/// <remarks>
/// AdvancedSettingsN returns IMsRdpClientAdvancedSettings(N-1); AdvancedSettings9 and
/// TransportSettings4 are supersets of the earlier interfaces. The non scriptable
/// interfaces come from a cast of GetOcx().
/// </remarks>
internal static class RdpSettingsApplier
{
    private const int DefaultRdpPort = 3389;

    // Keys read on top of the ones exposed by RdpFile.
    private const string LoadBalanceInfoKey = "loadbalanceinfo";
    private const string UseRedirectionServerNameKey = "use redirection server name";
    private const string PromptForCredentialsOnClientKey = "prompt for credentials on client";
    private const string PromptCredentialOnceKey = "promptcredentialonce";
    private const string SessionBppKey = "session bpp";
    private const string EnableCredSspSupportKey = "enablecredsspsupport";

    /// <summary>
    /// Applies <paramref name="file"/> and the display choices in <paramref name="options"/>
    /// to <paramref name="control"/>, creates the UltimateRDP virtual channel when
    /// <paramref name="createChannel"/> is true, and returns the desktop size that was
    /// set. With <paramref name="remoteApp"/>, the control runs that program in RemoteApp
    /// mode, with a desktop of the size of the virtual screen. Throws when the file has no
    /// address.
    /// </summary>
    public static Size Apply(RdpControl control, RdpFile file, SessionOptions options, bool createChannel, RemoteAppLaunch? remoteApp = null)
    {
        var log = new List<string>();

        var fullAddress = file.FullAddress;
        if (string.IsNullOrWhiteSpace(fullAddress))
        {
            throw new InvalidOperationException("The .rdp file has no \"full address\" setting.");
        }

        var (host, addressPort) = RdpAddress.Split(fullAddress);
        var port = addressPort ?? file.ServerPort ?? DefaultRdpPort;
        control.Server = host;
        control.AdvancedSettings9.RDPPort = port;
        log.Add(Invariant($"server={host} port={port}"));

        if (file.UserName is { Length: > 0 } userName)
        {
            TrySet(log, $"username={userName}", () => control.UserName = userName);
        }

        if (file.Domain is { Length: > 0 } domain)
        {
            TrySet(log, $"domain={domain}", () => control.Domain = domain);
        }

        var desktop = remoteApp is null ? ChooseDesktopSize(control.ClientSize, file) : RemoteAppDesktopSize(log);
        control.DesktopWidth = desktop.Width;
        control.DesktopHeight = desktop.Height;
        log.Add(Invariant($"desktop={desktop.Width}x{desktop.Height} (control {control.ClientSize.Width}x{control.ClientSize.Height})"));

        if (file.GetInt(SessionBppKey) is int bpp and (15 or 16 or 24 or 32))
        {
            TrySet(log, Invariant($"colorDepth={bpp}"), () => control.ColorDepth = bpp);
        }

        if (file.ScreenModeId == 2)
        {
            log.Add("screen mode id=2 (full screen) ignored, the session runs in the window");
        }

        if (remoteApp is not null)
        {
            ApplyRemoteApp(control, remoteApp, log);
        }

        var advanced = control.AdvancedSettings9;

        // Smart sizing scales the picture in "scale to window" mode; in "resize session"
        // mode the session follows the window and the picture is never scaled.
        TrySet(log, $"smartSizing={options.SmartSizing}", () => advanced.SmartSizing = options.SmartSizing);
        TrySet(log, $"redirectClipboard={file.RedirectClipboard}", () => advanced.RedirectClipboard = file.RedirectClipboard);
        ApplyDevices(control, file, options, log);
        if (remoteApp is null)
        {
            ApplyFullScreen(control, options, log);
        }
        else
        {
            ApplyRemoteAppFullScreen(control, log);
        }

        // The control leaves CredSSP (network level authentication) off unless asked;
        // mstsc turns it on unless the file says otherwise.
        var credSsp = file.GetBool(EnableCredSspSupportKey, true);
        TrySet(log, $"enableCredSspSupport={credSsp}", () => advanced.EnableCredSspSupport = credSsp);

        if (file.AuthenticationLevel is int authLevel and >= 0)
        {
            TrySet(log, Invariant($"authenticationLevel={authLevel}"), () => advanced.AuthenticationLevel = (uint)authLevel);
        }

        if (file.GetString(LoadBalanceInfoKey) is { Length: > 0 } loadBalanceInfo)
        {
            TrySet(log, $"loadbalanceinfo={loadBalanceInfo}", () => advanced.LoadBalanceInfo = LoadBalanceInfo.ToControlValue(loadBalanceInfo));
        }

        ApplyGateway(control, file, log);

        var ocx = control.GetOcx();
        if (ocx is IMsRdpClientNonScriptable5 nonScriptable)
        {
            TrySet(log, $"promptForCredentials={file.PromptForCredentials}", () => nonScriptable.PromptForCredentials = file.PromptForCredentials);

            if (file.GetInt(PromptForCredentialsOnClientKey) is int onClient)
            {
                TrySet(log, Invariant($"promptForCredsOnClient={onClient != 0}"), () => nonScriptable.PromptForCredsOnClient = onClient != 0);
            }

            ApplyDrives(control, nonScriptable, file, log);
            TrySet(log, $"useMultimon={options.UseMultimon}", () => nonScriptable.UseMultimon = options.UseMultimon);
        }
        else
        {
            log.Add("IMsRdpClientNonScriptable5 not available, prompt, drive and multimon settings left as they are");
            TrySet(log, $"redirectDrives={file.RequestsDriveRedirection}", () => advanced.RedirectDrives = file.RequestsDriveRedirection);
        }

        ApplyScaleFactors(ocx, options, log);

        if (ocx is IMsRdpPreferredRedirectionInfo redirectionInfo && file.GetInt(UseRedirectionServerNameKey) is int useName)
        {
            TrySet(log, Invariant($"useRedirectionServerName={useName != 0}"), () => redirectionInfo.UseRedirectionServerName = useName != 0);
        }

        if (createChannel)
        {
            control.CreateVirtualChannels(ProtocolConstants.ChannelName);
            log.Add($"virtual channel {ProtocolConstants.ChannelName} created");
        }

        ClientLog.Info("Settings applied: " + string.Join("; ", log));
        return desktop;
    }

    /// <summary>
    /// The desktop size is the control client size, so that the session fills the window
    /// at 100 %. The .rdp size is only used when the control is too small.
    /// </summary>
    public static Size ChooseDesktopSize(Size controlClientSize, RdpFile file)
    {
        int width;
        int height;
        if (controlClientSize.Width >= SessionDisplay.MinSize && controlClientSize.Height >= SessionDisplay.MinSize)
        {
            width = controlClientSize.Width;
            height = controlClientSize.Height;
        }
        else
        {
            width = file.DesktopWidth ?? 1024;
            height = file.DesktopHeight ?? 768;
        }

        // An even width avoids the padding some servers add to odd widths.
        var (clampedWidth, clampedHeight) = SessionDisplay.ClampSize(width, height);
        return new Size(clampedWidth, clampedHeight);
    }

    /// <summary>
    /// A RemoteApp session needs a desktop that covers every local monitor: the server puts
    /// the windows of the program where they are on the client.
    /// </summary>
    private static Size RemoteAppDesktopSize(List<string> log)
    {
        var screen = SystemInformation.VirtualScreen;
        var (width, height) = SessionDisplay.ClampSize(screen.Width, screen.Height);
        if (width != screen.Width || height != screen.Height)
        {
            log.Add(Invariant($"virtual screen {screen.Width}x{screen.Height} at ({screen.X}, {screen.Y}) brought within the limits of the control"));
        }

        return new Size(width, height);
    }

    /// <summary>
    /// RemoteApp mode, set before Connect: the control asks the server for the program
    /// itself, with the RemoteProgram2 values. The alternate shell is left alone; the server
    /// ignores it in RemoteApp mode.
    /// </summary>
    private static void ApplyRemoteApp(RdpControl control, RemoteAppLaunch remoteApp, List<string> log)
    {
        TrySet(log, "remoteProgramMode=True", () => control.RemoteProgram.RemoteProgramMode = true);
        var program = control.RemoteProgram2;
        if (remoteApp.Program is { } path)
        {
            TrySet(log, $"remoteApplicationProgram={path}", () => program.RemoteApplicationProgram = path);
        }

        if (remoteApp.Name is { } name)
        {
            TrySet(log, $"remoteApplicationName={name}", () => program.RemoteApplicationName = name);
        }

        if (remoteApp.Args is { } args)
        {
            TrySet(log, $"remoteApplicationArgs={args}", () => program.RemoteApplicationArgs = args);
        }

        if (remoteApp.File is { } target)
        {
            log.Add($"remoteapplicationfile={target} kept for ServerStartProgram");
        }
    }

    /// <summary>
    /// Sound, printers, serial ports, smart cards and the Windows keys.
    /// </summary>
    private static void ApplyDevices(RdpControl control, RdpFile file, SessionOptions options, List<string> log)
    {
        var advanced = control.AdvancedSettings9;
        TrySet(log, Invariant($"audioRedirectionMode={file.AudioMode}"), () => control.SecuredSettings2.AudioRedirectionMode = file.AudioMode);
        TrySet(log, $"audioCaptureRedirectionMode={file.AudioCaptureMode}", () => advanced.AudioCaptureRedirectionMode = file.AudioCaptureMode);
        TrySet(log, $"redirectPrinters={file.RedirectPrinters}", () => advanced.RedirectPrinters = file.RedirectPrinters);
        TrySet(log, $"redirectPorts={file.RedirectComPorts}", () => advanced.RedirectPorts = file.RedirectComPorts);
        TrySet(log, $"redirectSmartCards={file.RedirectSmartCards}", () => advanced.RedirectSmartCards = file.RedirectSmartCards);
        TrySet(log, Invariant($"keyboardHookMode={options.KeyboardHookMode}"), () => control.SecuredSettings2.KeyboardHookMode = options.KeyboardHookMode);
    }

    /// <summary>
    /// The control runs full screen itself (ContainerHandledFullScreen 0), with its
    /// connection bar shown and not pinned, so that the user can pin it.
    /// </summary>
    private static void ApplyFullScreen(RdpControl control, SessionOptions options, List<string> log)
    {
        var advanced = control.AdvancedSettings9;
        TrySet(log, "containerHandledFullScreen=0", () => advanced.ContainerHandledFullScreen = 0);
        TrySet(log, "displayConnectionBar=True", () => advanced.DisplayConnectionBar = true);
        TrySet(log, "pinConnectionBar=False", () => advanced.PinConnectionBar = false);
        TrySet(log, "connectionBarShowPinButton=True", () => advanced.ConnectionBarShowPinButton = true);
        TrySet(log, $"fullScreenTitle={options.FullScreenTitle}", () => control.FullScreenTitle = options.FullScreenTitle);
    }

    /// <summary>
    /// In RemoteApp mode the container handles full screen (ContainerHandledFullScreen 1), and
    /// never takes it. The first real run of 0.8.0, with 0, logged OnEnterFullScreenMode just
    /// before OnConnected, and the control showed its own UIMainClass window over a whole
    /// monitor, visible and empty, beside the window of the program: with 0 the control makes
    /// its own full screen window, and in RemoteApp mode it entered full screen by itself. With
    /// 1 it asks the container with OnRequestGoFullScreen instead, which the client ignores in
    /// RemoteApp mode, so that its windows stay children of the client window, which is hidden.
    /// </summary>
    private static void ApplyRemoteAppFullScreen(RdpControl control, List<string> log)
    {
        var advanced = control.AdvancedSettings9;
        TrySet(log, "containerHandledFullScreen=1 (RemoteApp)", () => advanced.ContainerHandledFullScreen = 1);
        TrySet(log, "fullScreen=False (RemoteApp)", () => control.FullScreen = false);
    }

    /// <summary>
    /// Both factors are VT_UI4 extended properties that cannot change once connected.
    /// </summary>
    private static void ApplyScaleFactors(object? ocx, SessionOptions options, List<string> log)
    {
        if (ocx is not IMsRdpExtendedSettings extended)
        {
            log.Add("IMsRdpExtendedSettings not available, scale factors left as they are");
            return;
        }

        TrySet(log, Invariant($"desktopScaleFactor={options.DesktopScaleFactor}"), () => SetExtended(extended, "DesktopScaleFactor", (uint)options.DesktopScaleFactor));
        TrySet(log, Invariant($"deviceScaleFactor={options.DeviceScaleFactor}"), () => SetExtended(extended, "DeviceScaleFactor", (uint)options.DeviceScaleFactor));
    }

    private static void SetExtended(IMsRdpExtendedSettings extended, string name, object value)
    {
        extended.set_Property(name, ref value);
    }

    /// <summary>
    /// The warning of Windows about the publisher and the shared resources of the connection,
    /// which the control shows before a RemoteApp connection, since 0.10.0. With
    /// <c>--trusted</c> on the command line it is turned off through
    /// IMsRdpClientNonScriptable3.ShowRedirectionWarningDialog and
    /// IMsRdpClientNonScriptable4.RedirectionWarningType (trusted publisher); otherwise nothing is
    /// set and Windows decides, as before. No key of a .rdp file reaches this. The question about
    /// the certificate of the server is another dialog and is never turned off here. One line of
    /// the log says what was decided and why.
    /// </summary>
    public static void ApplyRedirectionWarning(RdpControl control, bool trusted, string host)
    {
        if (!trusted)
        {
            ClientLog.Info($"Windows warning about the publisher and the shared resources ({host}): left to Windows, the launch has no --trusted; a .rdp file never turns it off.");
            return;
        }

        var log = new List<string>();
        if (control.GetOcx() is IMsRdpClientNonScriptable5 nonScriptable)
        {
            TrySet(log, "showRedirectionWarningDialog=False", () => nonScriptable.ShowRedirectionWarningDialog = false);
            TrySet(log, "redirectionWarningType=Trusted", () => nonScriptable.RedirectionWarningType = RedirectionWarningType.RedirectionWarningTypeTrusted);
        }
        else
        {
            log.Add("IMsRdpClientNonScriptable5 not available, left to Windows");
        }

        ClientLog.Info($"Windows warning about the publisher and the shared resources ({host}): not shown, --trusted on the command line ({string.Join("; ", log)}); the certificate question of the server is not affected.");
    }

    private static void ApplyGateway(RdpControl control, RdpFile file, List<string> log)
    {
        var transport = control.TransportSettings4;
        if (file.GatewayHostname is { Length: > 0 } gatewayHostname)
        {
            TrySet(log, $"gatewayHostname={gatewayHostname}", () => transport.GatewayHostname = gatewayHostname);
        }

        if (file.GatewayUsageMethod is int usage and >= 0)
        {
            TrySet(log, Invariant($"gatewayUsageMethod={usage}"), () => transport.GatewayUsageMethod = (uint)usage);
        }

        if (file.GatewayCredentialsSource is int source and >= 0)
        {
            TrySet(log, Invariant($"gatewayCredsSource={source}"), () => transport.GatewayCredsSource = (uint)source);
        }

        if (file.GatewayProfileUsageMethod is int profile and >= 0)
        {
            TrySet(log, Invariant($"gatewayProfileUsageMethod={profile}"), () => transport.GatewayProfileUsageMethod = (uint)profile);
        }

        if (file.GetInt(PromptCredentialOnceKey) is int once and >= 0)
        {
            TrySet(log, Invariant($"gatewayCredSharing={once}"), () => transport.GatewayCredSharing = (uint)once);
        }
    }

    private static void ApplyDrives(RdpControl control, IMsRdpClientNonScriptable5 nonScriptable, RdpFile file, List<string> log)
    {
        var advanced = control.AdvancedSettings9;
        if (!file.RequestsDriveRedirection)
        {
            TrySet(log, "redirectDrives=False (the .rdp file does not redirect drives)", () => advanced.RedirectDrives = false);
            return;
        }

        var selection = DriveSelection.Parse(file.DrivesToRedirect);
        TrySet(log, $"redirectDrives=True drivestoredirect=\"{file.DrivesToRedirect}\"", () => advanced.RedirectDrives = true);
        TrySet(log, $"dynamicDrives={selection.DynamicDrives}", () => nonScriptable.RedirectDynamicDrives = selection.DynamicDrives);

        try
        {
            var drives = nonScriptable.DriveCollection;
            if (drives.DriveCount == 0)
            {
                drives.RescanDrives(false);
            }

            var states = new List<string>();
            for (uint i = 0; i < drives.DriveCount; i++)
            {
                var drive = drives.DriveByIndex[i];
                var name = drive.Name ?? string.Empty;
                drive.RedirectionState = selection.Includes(name);
                states.Add($"{name}={drive.RedirectionState}");
            }

            log.Add("drives: " + (states.Count == 0 ? "none listed by the control" : string.Join(", ", states)));
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            log.Add(Invariant($"drive collection not available (0x{ex.HResult:X8}), every drive stays redirected"));
        }
    }

    /// <summary>
    /// Applies one optional setting. A value that the control refuses (E_INVALIDARG
    /// arrives as <see cref="ArgumentException"/>) is logged and skipped, so that one odd
    /// line of a .rdp file does not prevent the connection.
    /// </summary>
    private static void TrySet(List<string> log, string setting, Action apply)
    {
        try
        {
            apply();
            log.Add(setting);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            log.Add(Invariant($"{setting} refused by the control (0x{ex.HResult:X8}), skipped"));
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
