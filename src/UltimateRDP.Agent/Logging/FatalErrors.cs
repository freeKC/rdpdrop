using UltimateRDP.Agent.Drops;
using UltimateRDP.Agent.Interop;

namespace UltimateRDP.Agent.Logging;

/// <summary>
/// Last line of defence against errors nothing caught. The agent runs in every session
/// of a server, most of them without an UltimateRDP client, so it must never show an error
/// dialog: an error on a thread without a handler is logged, the simulated button is
/// released if a drag held it, and the process ends at once with
/// <see cref="ExitCode"/>, before Windows can show an error report. A task error that
/// nobody observed is only logged.
/// </summary>
internal static class FatalErrors
{
    /// <summary>Exit code of the agent after an unexpected error.</summary>
    public const int ExitCode = 1;

    private static FileLogger? _logger;
    private static int _ending;

    /// <summary>Installs the handlers. Call first thing in Main.</summary>
    public static void Install(FileLogger logger)
    {
        _logger = logger;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>Logs the error, releases the button and ends the process quietly.</summary>
    public static void EndProcess(string where, object? error)
    {
        if (Interlocked.Exchange(ref _ending, 1) == 0)
        {
            try
            {
                var exception = error as Exception;
                _logger?.Error(
                    $"Unexpected error {where}; the agent stops with exit code {ExitCode}." + (exception is null && error is not null ? " " + error : string.Empty),
                    exception);
            }
            catch (Exception)
            {
                // The process ends anyway.
            }

            InputInjector.ReleaseIfDown();
            try
            {
                // The key of the direct connection is left in the user key store otherwise.
                Direct.DirectCertificate.Delete(null);
            }
            catch (Exception)
            {
                // The process ends anyway; the next agent of the session replaces the key.
            }
        }

        Kernel32.TerminateProcess(Kernel32.GetCurrentProcess(), ExitCode);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var thread = Thread.CurrentThread;
        EndProcess($"on thread {thread.ManagedThreadId} ({thread.Name ?? (thread.IsThreadPoolThread ? "thread pool" : "unnamed")})", e.ExceptionObject);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            _logger?.Error("A background task failed and nobody observed it; the agent goes on.", e.Exception);
        }
        catch (Exception)
        {
            // A failure to log is ignored here.
        }

        e.SetObserved();
    }
}
