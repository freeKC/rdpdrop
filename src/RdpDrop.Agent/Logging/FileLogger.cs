using System.Globalization;
using System.Text;

namespace RdpDrop.Agent.Logging;

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
/// log must not stop the agent.
/// </summary>
internal sealed class FileLogger
{
    public const int RetentionDays = 14;

    private const string FilePrefix = "agent-";
    private const string FileExtension = ".log";
    private const string DateFormat = "yyyyMMdd";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _directory;
    private DateOnly _currentDay;

    public FileLogger(string directory)
    {
        _directory = directory;
    }

    /// <summary>Lines below this level are not written. Info by default.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public string Directory => _directory;

    public void Debug(string message) => Write(LogLevel.Debug, message);

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warn(string message) => Write(LogLevel.Warn, message);

    public void Error(string message, Exception? exception = null) =>
        Write(LogLevel.Error, exception is null ? message : message + Environment.NewLine + exception);

    public void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var now = DateTime.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(LevelText(level))
            .Append(" [")
            .Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture))
            .Append("] ")
            .Append(message)
            .AppendLine()
            .ToString();

        lock (_gate)
        {
            try
            {
                var today = DateOnly.FromDateTime(now);
                if (today != _currentDay)
                {
                    System.IO.Directory.CreateDirectory(_directory);
                    _currentDay = today;
                    DeleteOldFiles(today);
                }

                var path = Path.Combine(_directory, FilePrefix + now.ToString(DateFormat, CultureInfo.InvariantCulture) + FileExtension);
                File.AppendAllText(path, line, Utf8NoBom);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nowhere to report it; drop the line.
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
