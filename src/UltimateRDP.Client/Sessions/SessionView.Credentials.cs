using System.Runtime.InteropServices;
using MSTSCLib;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// Saved Windows credentials of the session host: applied before each connection when
/// Windows keeps a generic credential <c>TERMSRV/&lt;host&gt;</c>, otherwise the control
/// asks as before.
/// </summary>
internal sealed partial class SessionView
{
    private bool _savedCredentialsApplied;

    /// <summary>True when the client was started with <c>--ask-credentials</c> for this tab; read from 0.9.0 on.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AskCredentialsFromCommandLine { get; init; }

    /// <summary>Kinds of credentials Windows keeps for the host.</summary>
    public SavedCredentialKinds FindSavedCredentials() => SavedCredentials.Find(Host);

    /// <summary>
    /// Deletes the credentials Windows keeps for the host. The open session goes on;
    /// the next connection asks again.
    /// </summary>
    public bool ForgetSavedCredentials()
    {
        var deleted = SavedCredentials.Delete(Host);
        ShowDropStatus(deleted
            ? LocalText.Of(nameof(Strings.CredentialsForgotten), Host)
            : LocalText.Of(nameof(Strings.NoSavedCredentials), Host));

        return deleted;
    }

    /// <summary>
    /// Called after the .rdp settings were applied and before Connect. User name, domain
    /// and password come from the generic credential; the prompt is then turned off for
    /// this connection. Nothing of the credential is logged.
    /// </summary>
    private void ApplySavedCredentials()
    {
        if (_rdp.GetOcx() is not IMsRdpClientNonScriptable5 nonScriptable)
        {
            ClientLog.Warn($"Saved credentials for {Host} not looked up: IMsRdpClientNonScriptable5 is not available.");
            return;
        }

        using var credential = SavedCredentials.Read(Host);
        if (credential is null)
        {
            ForgetAppliedCredentials(nonScriptable);
            if ((SavedCredentials.Find(Host) & SavedCredentialKinds.DomainPassword) != 0)
            {
                ClientLog.Info($"Windows keeps a domain password credential for {Host}; its password is only readable by Windows, so the control asks as usual.");
            }

            return;
        }

        try
        {
            // Set first, so that a failure halfway still clears what reached the control.
            _savedCredentialsApplied = true;
            _rdp.UserName = credential.UserName;
            _rdp.Domain = credential.Domain;
            var hasPassword = false;
            credential.UsePassword(password =>
            {
                hasPassword = password.Length > 0;
                if (hasPassword)
                {
                    nonScriptable.ClearTextPassword = password;
                }
            });

            if (hasPassword)
            {
                nonScriptable.PromptForCredentials = false;
                ClientLog.Info($"Using saved credentials for {Host}");
            }
            else
            {
                ClientLog.Info($"The saved credential for {Host} has no password; only the user name is filled in.");
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or AxHost.InvalidActiveXStateException)
        {
            // The message of a COM error never holds the values that were set.
            ClientLog.Warn($"Saved credentials for {Host} refused by the control (0x{ex.HResult:X8}); the control asks instead.");
            ForgetAppliedCredentials(nonScriptable);
        }
    }

    /// <summary>
    /// After a connection that used a saved credential, puts back the user name and
    /// domain of the .rdp file and clears the password held by the control.
    /// </summary>
    private void ForgetAppliedCredentials(IMsRdpClientNonScriptable5 nonScriptable)
    {
        if (!_savedCredentialsApplied)
        {
            return;
        }

        _savedCredentialsApplied = false;
        try
        {
            _rdp.UserName = RdpFile.UserName ?? string.Empty;
            _rdp.Domain = RdpFile.Domain ?? string.Empty;
            nonScriptable.ResetPassword();
            ClientLog.Info($"Saved credentials for {Host} no longer applied.");
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or AxHost.InvalidActiveXStateException)
        {
            ClientLog.Warn($"Could not clear the credentials applied for {Host} (0x{ex.HResult:X8}).");
        }
    }
}
