using System.Globalization;
using System.Text;

namespace UltimateRDP.Client.Logging;

/// <summary>
/// Daily text log in %LOCALAPPDATA%\UltimateRDP\logs\client-yyyyMMdd.log. Files older than
/// <see cref="RetentionDays"/> days are deleted. Thread safe. Never throws: a failure
/// to write the log must not stop the client.
/// </summary>
internal static class ClientLog
{
    public const int RetentionDays = 14;

    private const string FilePrefix = "client-";
    private const string FileExtension = ".log";
    private const string DateFormat = "yyyyMMdd";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly object Gate = new();
    private static DateOnly _currentDay;

    /// <summary>Folder that holds the log files.</summary>
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UltimateRDP",
        "logs");

    /// <summary>True when the <c>logLevel</c> setting is <c>debug</c>: the debug lines are written too.</summary>
    public static bool DebugEnabled { get; set; }

    /// <summary>A line written only when <see cref="DebugEnabled"/> is true.</summary>
    public static void Debug(string message)
    {
        if (DebugEnabled)
        {
            Write("DEBUG", message);
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : message + Environment.NewLine + exception);

    private static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(level)
            .Append(" [")
            .Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture))
            .Append("] ")
            .Append(message)
            .AppendLine()
            .ToString();

        lock (Gate)
        {
            try
            {
                var today = DateOnly.FromDateTime(now);
                if (today != _currentDay)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    _currentDay = today;
                    DeleteOldFiles(today);
                }

                var path = Path.Combine(Directory, FilePrefix + now.ToString(DateFormat, CultureInfo.InvariantCulture) + FileExtension);
                AppendShared(path, Utf8NoBom.GetBytes(line));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nowhere to report it; drop the line.
            }
        }
    }

    /// <summary>
    /// Appends with the file shared for writing: a second start of the client, which hands its
    /// file to the first one and exits, writes a few lines to the same file meanwhile.
    /// </summary>
    private static void AppendShared(string path, byte[] bytes)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
                stream.Write(bytes);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(attempt * 5);
            }
        }
    }

    private static void DeleteOldFiles(DateOnly today)
    {
        var oldest = today.AddDays(-(RetentionDays - 1));
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, FilePrefix + "*" + FileExtension))
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
}
