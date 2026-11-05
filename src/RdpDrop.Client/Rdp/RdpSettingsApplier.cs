using System.Globalization;
using System.Runtime.InteropServices;
using MSTSCLib;
using RdpDrop.Client.Logging;
using RdpDrop.Core.Protocol;
using RdpDrop.Core.RdpFiles;

namespace RdpDrop.Client.Rdp;

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
    private const int MinDesktopSize = 200;
    private const int MaxDesktopSize = 8192;

    // Keys read on top of the ones exposed by RdpFile.
    private const string LoadBalanceInfoKey = "loadbalanceinfo";
    private const string UseRedirectionServerNameKey = "use redirection server name";
    private const string PromptForCredentialsOnClientKey = "prompt for credentials on client";
    private const string PromptCredentialOnceKey = "promptcredentialonce";
    private const string SessionBppKey = "session bpp";
    private const string RemoteApplicationModeKey = "remoteapplicationmode";
    private const string EnableCredSspSupportKey = "enablecredsspsupport";

    /// <summary>
    /// Applies <paramref name="file"/> to <paramref name="control"/>, creates the RdpDrop
    /// virtual channel when <paramref name="createChannel"/> is true, and returns the
    /// desktop size that was set. Throws when the file has no address.
    /// </summary>
    public static Size Apply(RdpControl control, RdpFile file, bool createChannel)
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

        var desktop = ChooseDesktopSize(control.ClientSize, file);
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

        if (file.GetInt(RemoteApplicationModeKey) == 1)
        {
            log.Add("remoteapplicationmode=1 ignored, RemoteApp is not supported, a full desktop is requested");
        }

        var advanced = control.AdvancedSettings9;

        // Smart sizing keeps the drop coordinates right after a resize; it is required.
        advanced.SmartSizing = true;
        log.Add("smartSizing=True");
        TrySet(log, $"redirectClipboard={file.RedirectClipboard}", () => advanced.RedirectClipboard = file.RedirectClipboard);

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
        }
        else
        {
            log.Add("IMsRdpClientNonScriptable5 not available, prompt and drive settings left as they are");
            TrySet(log, $"redirectDrives={file.RequestsDriveRedirection}", () => advanced.RedirectDrives = file.RequestsDriveRedirection);
        }

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
        if (controlClientSize.Width >= MinDesktopSize && controlClientSize.Height >= MinDesktopSize)
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
        width = Math.Clamp(width, MinDesktopSize, MaxDesktopSize) & ~1;
        height = Math.Clamp(height, MinDesktopSize, MaxDesktopSize);
        return new Size(width, height);
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
