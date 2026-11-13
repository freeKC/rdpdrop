using System.Globalization;
using System.Text;

namespace UltimateRDP.Agent.Logging;

internal enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// Writes one text file per day, agent-yyyyMMdd.log, and deletes files older than
/// <see cref="RetentionDays"/> days. Thread safe. Never throws: a failure to write the
/// log must not stop the agent. Each line goes to the file at once, so nothing waits in
/// memory when the process ends. Without a usable folder (no full path, or a folder
/// that cannot be created) the lines are dropped and the agent runs without a log.
/// Since 0.9.0 every line gives the session of the agent with its thread, as in
/// <c>[S3 12]</c>, and the processes of one user take turns on the file through a mutex
/// of that user across sessions.
/// </summary>
internal sealed class FileLogger
{
    public const int RetentionDays = 14;

    private const string FilePrefix = "agent-";
    private const string FileExtension = ".log";
    private const string DateFormat = "yyyyMMdd";

    /// <summary>Wait for the other writers of the user, at most.</summary>
    private const int MutexWaitMilliseconds = 1000;

    /// <summary>Opens of the file tried before the line is dropped.</summary>
    private const int MaxOpenAttempts = 4;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The session of the agent, in every line, since the agents of one user in several sessions write the same file.</summary>
    private static readonly string SessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture);

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly bool _usable;
    private DateOnly _currentDay;
    private DateOnly _failedDay;
    private Mutex? _fileMutex;
    private bool _fileMutexFailed;

    public FileLogger(string directory)
    {
        _directory = directory;
        _usable = Path.IsPathFullyQualified(directory);
    }

    /// <summary>Lines below this level are not written. Info by default.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public string Directory => _directory;

    /// <summary>True when a line of <paramref name="level"/> would be written.</summary>
    public bool IsEnabled(LogLevel level) => _usable && level >= MinimumLevel;

    public void Debug(string message) => Write(LogLevel.Debug, message);

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warn(string message) => Write(LogLevel.Warn, message);

    public void Error(string message, Exception? exception = null) =>
        Write(LogLevel.Error, exception is null ? message : message + Environment.NewLine + exception);

    public void Write(LogLevel level, string message)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        try
        {
            var now = DateTime.Now;
            var line = new StringBuilder()
                .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(LevelText(level))
                .Append(" [S")
                .Append(SessionId)
                .Append(' ')
                .Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture))
                .Append("] ")
                .Append(message)
                .AppendLine()
                .ToString();

            lock (_gate)
            {
                var held = AcquireFileMutex();
                try
                {
                    WriteLocked(now, line);
                }
                finally
                {
                    if (held)
                    {
                        _fileMutex!.ReleaseMutex();
                    }
                }
            }
        }
        catch (Exception)
        {
            // Nowhere to report it; drop the line.
        }
    }

    /// <summary>
    /// The agents of one user in several sessions, a desktop and a RemoteApp session for
    /// example, and a copy started with --send, write the same file: a mutex of the user
    /// across sessions makes them take turns. Without it, a line is still written, and the
    /// file is opened so that another writer does not make it fail.
    /// </summary>
    private bool AcquireFileMutex()
    {
        if (_fileMutex is null && !_fileMutexFailed)
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                _fileMutex = new Mutex(false, @"Global\UltimateRDP.AgentLog." + (identity.User?.Value ?? "unknown"));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                _fileMutexFailed = true;
            }
        }

        if (_fileMutex is null)
        {
            return false;
        }

        try
        {
            return _fileMutex.WaitOne(MutexWaitMilliseconds);
        }
        catch (AbandonedMutexException)
        {
            // A writer ended without releasing it; this one owns it now.
            return true;
        }
    }

    private void WriteLocked(DateTime now, string line)
    {
        var today = DateOnly.FromDateTime(now);
        if (today == _failedDay)
        {
            // The folder could not be created today; try again tomorrow only.
            return;
        }

        if (today != _currentDay)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_directory);
            }
            catch (Exception)
            {
                _failedDay = today;
                return;
            }

            _currentDay = today;
            try
            {
                DeleteOldFiles(today);
            }
            catch (Exception)
            {
                // The old files are tried again tomorrow; today's line still goes out.
            }
        }

        var path = Path.Combine(_directory, FilePrefix + now.ToString(DateFormat, CultureInfo.InvariantCulture) + FileExtension);
        var bytes = Utf8NoBom.GetBytes(line);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Shared for writing too: a writer without the mutex, or a reader, does not make the open fail.
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
                stream.Write(bytes);
                return;
            }
            catch (IOException) when (attempt < MaxOpenAttempts)
            {
                Thread.Sleep(attempt * 5);
            }
        }
    }

    private void DeleteOldFiles(DateOnly today)
    {
        var oldest = today.AddDays(-(RetentionDays - 1));
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, FilePrefix + "*" + FileExtension))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length != FilePrefix.Length + DateFormat.Length)
            {
                continue;
            }

            if (DateOnly.TryParseExact(name[FilePrefix.Length..], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day < oldest)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Try again tomorrow.
                }
            }
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        _ => "ERROR",
    };
}
