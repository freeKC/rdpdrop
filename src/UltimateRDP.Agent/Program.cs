using System.Text;
using Microsoft.Win32;
using UltimateRDP.Agent.Localization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Resources;
using UltimateRDP.Agent.Settings;
using UltimateRDP.Agent.Setup;
using UltimateRDP.Agent.Tray;

namespace UltimateRDP.Agent;

internal static class Program
{
    private const string ExecutableName = "UltimateRDP.Agent.exe";
    private const string CurrentUserRoot = "HKEY_CURRENT_USER";
    private const string LocalMachineRoot = "HKEY_LOCAL_MACHINE";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        var logger = new FileLogger(AgentInfo.LogDirectory);
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

    private static int Run(FileLogger logger)
    {
        using var instanceMutex = new Mutex(initiallyOwned: true, AgentInfo.InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            logger.Info("The agent is already running in this session; this second copy exits.");
            return 0;
        }

        try
        {
            using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AgentInfo.ExitEventName);
            var settings = AgentSettings.Load(AgentInfo.SettingsPath, logger);
            logger.MinimumLevel = settings.LogLevel;

            Application.ThreadException += (_, e) => logger.Error("Unhandled error on the UI thread.", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                logger.Error("Unhandled error; the agent stops.", e.ExceptionObject as Exception);

            using var context = new TrayContext(logger, settings, exitEvent);
            Application.Run(context);
            return 0;
        }
        finally
        {
            instanceMutex.ReleaseMutex();
        }
    }

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
        return Run(logger);
    }

    private static int InstallAllUsers(FileLogger logger)
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
        CommandLineOutput.Info(LocalText.Of(nameof(Strings.InstalledForAllUsers), StartupRegistration.ExecutablePath).ForUser());
        return 0;
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

        messages.Add(StopRunningInstance() ? LocalText.Of(nameof(Strings.AgentStopped)) : LocalText.Of(nameof(Strings.AgentStillRunning)));
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
    /// Asks the agent running in this session to exit and waits for it. Returns true
    /// when no agent runs any more.
    /// </summary>
    private static bool StopRunningInstance()
    {
        if (!Mutex.TryOpenExisting(AgentInfo.InstanceMutexName, out var mutex))
        {
            return true;
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
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // The agent ended without releasing the mutex; it is gone all the same.
            }

            mutex.ReleaseMutex();
            return true;
        }
    }
}
