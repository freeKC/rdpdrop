using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using UltimateRDP.Agent.Interop;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Resources;
using UltimateRDP.Core.Agent;

namespace UltimateRDP.Agent.Send;

/// <summary>
/// The shortcut "My PC (UltimateRDP)" of the Send To menu of Explorer, since 0.9.0. It runs
/// <c>UltimateRDP.Agent.exe --send</c> with the selected files, which hands them to the agent
/// of the session. It exists only while a client that receives files is connected.
/// </summary>
/// <remarks>
/// <para>
/// The Send To folder belongs to the user, not to the session, and the agents of one user in
/// several sessions share it. Each agent that wants the shortcut writes a claim, a file named
/// after its session in <c>%LOCALAPPDATA%\UltimateRDP\sendto-claims</c> that holds its process
/// id and start time; the shortcut goes when the last live claim goes. A claim whose process
/// is gone, as after an agent that ended badly, does not count, and the next start removes it.
/// </para>
/// <para>Runs on the UI thread, which is a single threaded apartment, as the shell link object needs.</para>
/// </remarks>
internal sealed class SendToShortcut
{
    private const string Extension = ".lnk";

    private readonly FileLogger _logger;
    private readonly string _claims;
    private readonly string _claimPath;
    private bool _claimed;

    public SendToShortcut(FileLogger logger, string claimsFolder)
    {
        _logger = logger;
        _claims = claimsFolder;
        _claimPath = Path.Combine(claimsFolder, "S" + Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture) + ".claim");
    }

    /// <summary>The Send To folder of the user, or null when Windows does not give one.</summary>
    private static string? Folder
    {
        get
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
            return string.IsNullOrEmpty(folder) ? null : folder;
        }
    }

    /// <summary>
    /// At the start of the agent: removes the claims of agents that are gone, and the
    /// shortcut, in any language, when no live claim is left. Touches nothing when no claim
    /// folder and no shortcut exist.
    /// </summary>
    public void RemoveLeftovers()
    {
        try
        {
            var live = LiveClaims(deleteStale: true);
            if (live == 0)
            {
                DeleteShortcuts("no agent of this user claims it, at the start of the agent");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Send To shortcut: the leftovers cannot be checked. {ex.Message}");
        }
    }

    /// <summary>A client that receives files is connected: the claim of this session, and the shortcut.</summary>
    public void Claim()
    {
        if (Folder is not { } folder)
        {
            _logger.Warn("Send To shortcut: Windows gives no Send To folder for this user; the shortcut is not created.");
            return;
        }

        try
        {
            Directory.CreateDirectory(_claims);
            using var process = Process.GetCurrentProcess();
            File.WriteAllText(_claimPath, string.Create(CultureInfo.InvariantCulture, $"{process.Id} {process.StartTime.ToUniversalTime().Ticks}"));
            _claimed = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Send To shortcut: the claim {_claimPath} cannot be written; the shortcut may go when another agent of this user stops. {ex.Message}");
        }

        var name = SendToClient.ShortcutName(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        var path = Path.Combine(folder, name + Extension);
        try
        {
            Create(path);
            _logger.Info($"Send To shortcut created: {path}, running {Environment.ProcessPath} --send.");
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            _logger.Warn($"Send To shortcut: {path} cannot be created. {ex.Message}");
        }
    }

    /// <summary>The client left, the setting changed or the agent stops: the claim goes, and the shortcut with the last live claim.</summary>
    public void Release(string reason)
    {
        if (!_claimed)
        {
            return;
        }

        _claimed = false;
        try
        {
            File.Delete(_claimPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Send To shortcut: the claim {_claimPath} cannot be deleted. {ex.Message}");
        }

        try
        {
            var live = LiveClaims(deleteStale: false);
            if (live == 0)
            {
                DeleteShortcuts(reason);
            }
            else
            {
                _logger.Info($"Send To shortcut kept: {live} other agent(s) of this user still have a client that receives files.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Send To shortcut: the claims cannot be read; the shortcut stays. {ex.Message}");
        }
    }

    private static void Create(string path)
    {
        var exe = Environment.ProcessPath ?? throw new IOException("The path of the agent is not known.");
        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            link.SetPath(exe);
            link.SetArguments("--send");
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? string.Empty);
            link.SetDescription(Strings.SendShortcutDescription);
            link.SetIconLocation(exe, 0);
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>Claims whose agent still runs; with <paramref name="deleteStale"/>, the others are deleted.</summary>
    private int LiveClaims(bool deleteStale)
    {
        if (!Directory.Exists(_claims))
        {
            return 0;
        }

        var live = 0;
        foreach (var claim in Directory.EnumerateFiles(_claims, "S*.claim"))
        {
            if (IsLive(claim))
            {
                live++;
            }
            else if (deleteStale)
            {
                try
                {
                    File.Delete(claim);
                    _logger.Info($"Send To shortcut: the claim {claim} of an agent that is gone was removed.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Tried again at the next start.
                }
            }
        }

        return live;
    }

    /// <summary>True when the process of the claim runs, with the start time of the claim, and is not this one before its claim.</summary>
    private bool IsLive(string claim)
    {
        try
        {
            var parts = File.ReadAllText(claim).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            {
                return false;
            }

            if (string.Equals(claim, _claimPath, StringComparison.OrdinalIgnoreCase) && !_claimed)
            {
                return false;
            }

            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private void DeleteShortcuts(string reason)
    {
        if (Folder is not { } folder)
        {
            return;
        }

        foreach (var name in SendToClient.AllShortcutNames)
        {
            var path = Path.Combine(folder, name + Extension);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _logger.Info($"Send To shortcut removed: {path}, {reason}.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warn($"Send To shortcut: {path} cannot be removed. {ex.Message}");
            }
        }
    }
}
