using Microsoft.Win32;
using RdpDrop.Agent.Logging;
using RdpDrop.Agent.Settings;
using RdpDrop.Agent.Setup;
using RdpDrop.Agent.Tray;

namespace RdpDrop.Agent;

internal static class Program
{
    private const string Usage =
        "Usage: RdpDrop.Agent.exe [option]\n\n"
        + "  (no option)          Run the agent in the tray.\n"
        + "  --install            Start the agent with your session, then run it.\n"
        + "  --uninstall          Stop starting the agent with your session and stop it.\n"
        + "  --install-all-users  Start the agent with every user session (elevated prompt).\n"
        + "  --help               Show this text.";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        var logger = new FileLogger(AgentInfo.LogDirectory);
        if (args.Length > 1)
        {
            CommandLineOutput.Error("Too many arguments.\n\n" + Usage);
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
                CommandLineOutput.Info(Usage);
                return 0;

            default:
                CommandLineOutput.Error($"Unknown option \"{args[0]}\".\n\n" + Usage);
                return 2;
        }
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
            CommandLineOutput.Error($"Cannot write HKEY_CURRENT_USER\\{AgentInfo.RunKeyPath}. {error}");
            return 1;
        }

        logger.Info($"Installed for the current user: {StartupRegistration.Command}.");
        CommandLineOutput.Info("The agent now starts with your session.", showBoxWithoutConsole: false);
        return Run(logger);
    }

    private static int InstallAllUsers(FileLogger logger)
    {
        var error = StartupRegistration.Register(Registry.LocalMachine);
        if (error is not null)
        {
            CommandLineOutput.Error(
                $"Cannot write HKEY_LOCAL_MACHINE\\{AgentInfo.RunKeyPath}. {error}\n\n"
                + "Run this command again from an elevated prompt (Run as administrator).");
            return 1;
        }

        logger.Info($"Installed for all users: {StartupRegistration.Command}.");
        CommandLineOutput.Info(
            "The agent now starts with every user session, from\n"
            + StartupRegistration.ExecutablePath + "\n\n"
            + "Keep the executable in a folder that every user can read, such as Program Files. "
            + "Users already signed in get the agent at their next sign in.");
        return 0;
    }

    private static int Uninstall(FileLogger logger)
    {
        var exitCode = 0;
        var messages = new List<string>();

        var error = StartupRegistration.Unregister(Registry.CurrentUser);
        if (error is null)
        {
            messages.Add("The agent no longer starts with your session.");
        }
        else
        {
            messages.Add($"Cannot remove the value from HKEY_CURRENT_USER\\{AgentInfo.RunKeyPath}. {error}");
            exitCode = 1;
        }

        if (StartupRegistration.IsRegistered(Registry.LocalMachine))
        {
            var machineError = StartupRegistration.Unregister(Registry.LocalMachine);
            if (machineError is null)
            {
                messages.Add("The agent no longer starts with every user session.");
            }
            else
            {
                messages.Add(
                    "The agent is still set to start for all users, under HKEY_LOCAL_MACHINE. "
                    + "Run --uninstall from an elevated prompt to remove that too.");
                exitCode = 1;
            }
        }

        messages.Add(StopRunningInstance() ? "The running agent was stopped." : "The agent is still running; exit it from its tray menu.");
        logger.Info("Uninstall: " + string.Join(" ", messages));

        var text = string.Join("\n", messages);
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
