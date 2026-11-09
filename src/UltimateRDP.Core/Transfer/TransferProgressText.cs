namespace UltimateRDP.Core.Transfer;

/// <summary>
/// Templates of the texts of the transfer progress window, in the language of the user
/// interface. Each one is a composite format string.
/// </summary>
public sealed record TransferTextFormats
{
    /// <summary>
    /// The progress once the rate is known, for example "{0} %, {1:0.0} MB/s, {2}": {0} is
    /// the percentage, {1} the rate in megabytes per second, {2} the text of <see cref="Remaining"/>.
    /// </summary>
    public required string Progress { get; init; }

    /// <summary>The progress while the rate is not known yet, for example "{0} %".</summary>
    public required string ProgressWithoutRate { get; init; }

    /// <summary>The time left, for example "{0} left", {0} being a duration.</summary>
    public required string Remaining { get; init; }

    /// <summary>A duration under a minute, for example "{0} s".</summary>
    public required string Seconds { get; init; }

    /// <summary>A whole number of minutes, for example "{0} min".</summary>
    public required string Minutes { get; init; }

    /// <summary>Minutes and seconds, for example "{0} min {1} s".</summary>
    public required string MinutesSeconds { get; init; }

    /// <summary>Hours and minutes, for example "{0} h {1} min".</summary>
    public required string HoursMinutes { get; init; }
}

/// <summary>
/// Texts of the transfer progress window: the percentage, the rate and the time left, and
/// the duration of a finished transfer, from the templates of the user interface language
/// and the numbers formatted in the given culture.
/// </summary>
public static class TransferProgressText
{
    /// <summary>Bytes in a megabyte, as the rates are shown.</summary>
    public const double BytesPerMegabyte = 1_000_000;

    /// <summary>
    /// "42 %, 2.6 MB/s, 1 min 40 s left", or "42 %" while the rate is not known.
    /// </summary>
    public static string Describe(TransferProgress progress, TransferTextFormats formats, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(formats);
        return progress.BytesPerSecond is { } rate && progress.Remaining is { } remaining
            ? string.Format(culture, formats.Progress, progress.Percent, rate / BytesPerMegabyte, TimeLeftText(remaining, formats, culture))
            : string.Format(culture, formats.ProgressWithoutRate, progress.Percent);
    }

    /// <summary>"1 min 40 s left", rounded as <see cref="TimeLeft.Detailed"/> does.</summary>
    public static string TimeLeftText(TimeSpan remaining, TransferTextFormats formats, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(formats);
        return string.Format(culture, formats.Remaining, Format(TimeLeft.Detailed(remaining), formats, culture));
    }

    /// <summary>
    /// "3 min 13 s": a duration rounded to the second, at least one second; from an
    /// hour on, rounded to the minute.
    /// </summary>
    public static string Duration(TimeSpan elapsed, TransferTextFormats formats, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(formats);
        var seconds = (long)Math.Max(1, Math.Round(Math.Min(elapsed.TotalSeconds, int.MaxValue), MidpointRounding.AwayFromZero));
        TimeLeft time;
        if (seconds < 60)
        {
            time = new TimeLeft(TimeLeftUnit.Seconds, 0, 0, (int)seconds);
        }
        else if (seconds < 3600)
        {
            time = new TimeLeft(TimeLeftUnit.Minutes, 0, (int)(seconds / 60), (int)(seconds % 60));
        }
        else
        {
            var minutes = (seconds + 30) / 60;
            time = new TimeLeft(TimeLeftUnit.Hours, (int)(minutes / 60), (int)(minutes % 60), 0);
        }

        return Format(time, formats, culture);
    }

    /// <summary>A rate in megabytes per second, from <paramref name="bytes"/> sent in <paramref name="seconds"/>; zero without time.</summary>
    public static double MegabytesPerSecond(long bytes, double seconds) =>
        seconds > 0 ? bytes / BytesPerMegabyte / seconds : 0;

    private static string Format(TimeLeft time, TransferTextFormats formats, IFormatProvider culture) => time.Unit switch
    {
        TimeLeftUnit.Seconds => string.Format(culture, formats.Seconds, time.Seconds),
        TimeLeftUnit.Minutes when time.Seconds == 0 => string.Format(culture, formats.Minutes, time.Minutes),
        TimeLeftUnit.Minutes => string.Format(culture, formats.MinutesSeconds, time.Minutes, time.Seconds),
        _ => string.Format(culture, formats.HoursMinutes, time.Hours, time.Minutes),
    };
}
