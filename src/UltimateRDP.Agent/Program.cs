using System.Text;
using Microsoft.Win32;
using UltimateRDP.Agent.Drops;
using UltimateRDP.Agent.Interop;
using UltimateRDP.Agent.Localization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Resources;
using UltimateRDP.Agent.Settings;
using UltimateRDP.Agent.Setup;
using UltimateRDP.Agent.Tray;
using UltimateRDP.Core.Agent;

namespace UltimateRDP.Agent;

internal static class Program
{
    private const string ExecutableName = "UltimateRDP.Agent.exe";
    private const string CurrentUserRoot = "HKEY_CURRENT_USER";
    private const string LocalMachineRoot = "HKEY_LOCAL_MACHINE";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Without an option the agent runs, and shows nothing: no window, no message box, no
    /// console output, whatever fails. The options print their result, on the console of
    /// the parent process or in a message box.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var logger = new FileLogger(AgentInfo.LogDirectory);
        FatalErrors.Install(logger);

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // Without a handler, WinForms would show its error dialog for an error in a
        // handler of the UI thread. The drag threads install their own.
        Application.ThreadException += (_, e) => logger.Error("Unexpected error on the UI thread; the agent goes on.", e.Exception);

        if (args.Length > 1)
        {
            CommandLineOutput.Error(Strings.TooManyArguments + "\n\n" + Usage());
            return 2;
        }

        var option = args.Length == 0 ? string.Empty : args[0].Trim().ToLowerInvariant();
        switch (option)
        {
            case "":
                return Run(logger);

            case "--install":
                return Install(logger);

            case "--uninstall":
                return Uninstall(logger);

            case "--install-all-users":
                return InstallAllUsers(logger);

            case "--status":
                return Status(logger);

            case "--help" or "-h" or "/?":
                CommandLineOutput.Info(Usage());
                return 0;

            default:
                CommandLineOutput.Error(LocalText.Of(nameof(Strings.UnknownOption), args[0]).ForUser() + "\n\n" + Usage());
                return 2;
        }
    }

    /// <summary>
    /// The list of options, in the Windows display language. The option names stay as they
    /// are; the descriptions are aligned after the longest one.
    /// </summary>
    private static string Usage()
    {
        (string Name, string Description)[] rows =
        [
            (Strings.UsageNoOption, Strings.UsageRun),
            ("--install", Strings.UsageInstall),
            ("--uninstall", Strings.UsageUninstall),
            ("--install-all-users", Strings.UsageInstallAllUsers),
            ("--status", Strings.UsageStatus),
            ("--help", Strings.UsageHelp),
        ];

        var width = rows.Max(row => row.Name.Length) + 2;
        var text = new StringBuilder(LocalText.Of(nameof(Strings.UsageTitle), ExecutableName).ForUser()).Append("\n\n");
        foreach (var (name, description) in rows)
        {
            text.Append("  ").Append(name.PadRight(width)).Append(description).Append('\n');
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Runs the agent until it is asked to exit or the session ends. Every failure is
    /// logged and ends the agent quietly with exit code 1.
    /// </summary>
    private static int Run(FileLogger logger)
    {
        Mutex instanceMutex;
        try
        {
            instanceMutex = new Mutex(initiallyOwned: true, AgentInfo.InstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                instanceMutex.Dispose();
                logger.Info("The agent is already running in this session; this second copy exits.");
                return 0;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            logger.Warn($"Cannot create the single instance mutex; this copy exits. {ex.Message}");
            return FatalErrors.ExitCode;
        }

        try
        {
            var settings = AgentSettings.Load(AgentInfo.SettingsPath, logger);
            logger.MinimumLevel = settings.LogLevel;
            logger.Info($"{AgentInfo.ProductName} {AgentInfo.Version} started in session {SessionId()}; {settings}.");

            using var exitEvent = CreateExitEvent(logger);
            using var context = new TrayContext(logger, settings, exitEvent);
            context.Run();
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error("The agent stopped on an unexpected error.", ex);
            InputInjector.ReleaseIfDown();
            return FatalErrors.ExitCode;
        }
        finally
        {
            try
            {
                instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned any more; nothing to release.
            }

            instanceMutex.Dispose();
        }
    }

    /// <summary>The event that --uninstall sets to stop the agent, or null when it cannot be created.</summary>
    private static EventWaitHandle? CreateExitEvent(FileLogger logger)
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, AgentInfo.ExitEventName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            logger.Warn($"Cannot create the exit event; --uninstall will not stop this agent. {ex.Message}");
            return null;
        }
    }

    private static string SessionId() =>
        Kernel32.ProcessIdToSessionId((uint)Environment.ProcessId, out var session) ? session.ToString(System.Globalization.CultureInfo.InvariantCulture) : "(unknown)";

    private static int Install(FileLogger logger)
    {
        var error = StartupRegistration.Register(Registry.CurrentUser);
        if (error is not null)
        {
            CommandLineOutput.Error(LocalText.Of(nameof(Strings.CannotWriteKey), RunKey(CurrentUserRoot), error).ForUser());
            return 1;
        }

        logger.Info($"Installed for the current user: {StartupRegistration.Command}.");
        CommandLineOutput.Info(Strings.InstalledForUser, showBoxWithoutConsole: false);
        CommandLineOutput.DetachFromParentConsole();
        return Run(logger);
    }

    /// <summary>
    /// Writes the Run value under HKEY_LOCAL_MACHINE. Running it again changes nothing.
    /// Refuses an executable inside a user profile, and removes the Run value of the
    /// current user, which would start a second copy.
    /// </summary>
    private static int InstallAllUsers(FileLogger logger)
    {
        var path = StartupRegistration.ExecutablePath;
        if (InstallLocation.IsUnderAny(path, StartupRegistration.ProfileRoots()))
        {
            logger.Warn($"Install for all users refused: {path} is inside a user profile.");
            CommandLineOutput.Error(LocalText.Of(nameof(Strings.InstallPathUnderProfile), path, InstallLocation.SuggestedFolder).ForUser());
            return 1;
        }

        var messages = new List<LocalText>();
        if (string.Equals(StartupRegistration.Read(Registry.LocalMachine), StartupRegistration.Command, StringComparison.OrdinalIgnoreCase))
        {
            logger.Info($"Install for all users: already registered with {StartupRegistration.Command}.");
            messages.Add(LocalText.Of(nameof(Strings.AlreadyInstalledForAllUsers), path));
        }
        else
        {
            var error = StartupRegistration.Register(Registry.LocalMachine);
            if (error is not null)
            {
                CommandLineOutput.Error(
                    LocalText.Of(nameof(Strings.CannotWriteKey), RunKey(LocalMachineRoot), error).ForUser()
                    + "\n\n" + Strings.RunElevated);
                return 1;
            }

            logger.Info($"Installed for all users: {StartupRegistration.Command}.");
            messages.Add(LocalText.Of(nameof(Strings.InstalledForAllUsers), path));
        }

        if (StartupRegistration.IsRegistered(Registry.CurrentUser))
        {
            var userError = StartupRegistration.Unregister(Registry.CurrentUser);
            var message = userError is null
                ? LocalText.Of(nameof(Strings.RemovedUserRunValue), RunKey(CurrentUserRoot))
                : LocalText.Of(nameof(Strings.CannotRemoveValue), RunKey(CurrentUserRoot), userError);
            logger.Info($"Install for all users: {message}");
            messages.Add(message);
        }

        CommandLineOutput.Info(string.Join("\n\n", messages.Select(message => message.ForUser())));
        return 0;
    }

    /// <summary>Prints the version, the Run values, the files and whether an agent runs in this session.</summary>
    private static int Status(FileLogger logger)
    {
        var notSet = LocalText.Of(nameof(Strings.StatusNotSet));
        var settingsExist = AgentSettings.FileExists(AgentInfo.SettingsPath);
        var trayIcon = settingsExist ? AgentSettings.Load(AgentInfo.SettingsPath, logger).TrayIcon : TrayIconMode.Auto;
        var session = SessionId();

        LocalText[] lines =
        [
            LocalText.Of(nameof(Strings.StatusVersion), AgentInfo.Version),
            LocalText.Of(nameof(Strings.StatusExecutable), Environment.ProcessPath ?? string.Empty),
            LocalText.Of(nameof(Strings.StatusRunCurrentUser), RunKey(CurrentUserRoot), (object?)StartupRegistration.Read(Registry.CurrentUser) ?? notSet),
            LocalText.Of(nameof(Strings.StatusRunAllUsers), RunKey(LocalMachineRoot), (object?)StartupRegistration.Read(Registry.LocalMachine) ?? notSet),
            LocalText.Of(settingsExist ? nameof(Strings.StatusSettings) : nameof(Strings.StatusSettingsAbsent), AgentInfo.SettingsPath),
            LocalText.Of(nameof(Strings.StatusTrayIcon), TrayIconModes.ToSetting(trayIcon)),
            LocalText.Of(nameof(Strings.StatusLogs), AgentInfo.LogDirectory),
            LocalText.Of(IsRunningInThisSession() ? nameof(Strings.StatusRunning) : nameof(Strings.StatusNotRunning), session),
        ];

        CommandLineOutput.Info(AgentInfo.ProductName + "\n" + string.Join("\n", lines.Select(line => line.ForUser())));
        return 0;
    }

    /// <summary>True when the single instance mutex of this session exists.</summary>
    private static bool IsRunningInThisSession()
    {
        try
        {
            if (Mutex.TryOpenExisting(AgentInfo.InstanceMutexName, out var mutex))
            {
                mutex.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // It exists, but belongs to someone else.
            return true;
        }
    }

    private static int Uninstall(FileLogger logger)
    {
        var exitCode = 0;
        var messages = new List<LocalText>();

        var error = StartupRegistration.Unregister(Registry.CurrentUser);
        if (error is null)
        {
            messages.Add(LocalText.Of(nameof(Strings.UninstalledForUser)));
        }
        else
        {
            messages.Add(LocalText.Of(nameof(Strings.CannotRemoveValue), RunKey(CurrentUserRoot), error));
            exitCode = 1;
        }

        if (StartupRegistration.IsRegistered(Registry.LocalMachine))
        {
            var machineError = StartupRegistration.Unregister(Registry.LocalMachine);
            if (machineError is null)
            {
                messages.Add(LocalText.Of(nameof(Strings.UninstalledForAllUsers)));
            }
            else
            {
                messages.Add(LocalText.Of(nameof(Strings.StillInstalledForAllUsers)));
                exitCode = 1;
            }
        }

        var stop = StopRunningInstance();
        if (stop == StopResult.StillRunning)
        {
            exitCode = 1;
        }

        messages.Add(stop switch
        {
            StopResult.NotRunning => LocalText.Of(nameof(Strings.AgentNotRunning)),
            StopResult.Stopped => LocalText.Of(nameof(Strings.AgentStopped)),
            _ => LocalText.Of(nameof(Strings.AgentStillRunning)),
        });
        logger.Info("Uninstall: " + string.Join(" ", messages));

        var text = string.Join("\n", messages.Select(message => message.ForUser()));
        if (exitCode == 0)
        {
            CommandLineOutput.Info(text);
        }
        else
        {
            CommandLineOutput.Error(text);
        }

        return exitCode;
    }

    private static string RunKey(string root) => root + "\\" + AgentInfo.RunKeyPath;

    /// <summary>
    /// Asks the agent running in this session to exit and waits for it.
    /// </summary>
    private static StopResult StopRunningInstance()
    {
        Mutex? mutex;
        try
        {
            if (!Mutex.TryOpenExisting(AgentInfo.InstanceMutexName, out mutex))
            {
                return StopResult.NotRunning;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // It exists, but belongs to someone else.
            return StopResult.StillRunning;
        }

        using (mutex)
        {
            if (EventWaitHandle.TryOpenExisting(AgentInfo.ExitEventName, out var exitEvent))
            {
                using (exitEvent)
                {
                    exitEvent.Set();
                }
            }

            try
            {
                if (!mutex.WaitOne(StopTimeout))
                {
                    return StopResult.StillRunning;
                }
            }
            catch (AbandonedMutexException)
            {
                // The agent ended without releasing the mutex; it is gone all the same.
            }

            mutex.ReleaseMutex();
            return StopResult.Stopped;
        }
    }

    private enum StopResult
    {
        NotRunning,
        Stopped,
        StillRunning,
    }
}
