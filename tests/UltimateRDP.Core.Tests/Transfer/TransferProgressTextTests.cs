using System.Globalization;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

public class TransferProgressTextTests
{
    private static readonly TransferTextFormats English = new()
    {
        Progress = "{0} %, {1:0.0} MB/s, {2}",
        ProgressWithoutRate = "{0} %",
        Remaining = "{0} left",
        Seconds = "{0} s",
        Minutes = "{0} min",
        MinutesSeconds = "{0} min {1} s",
        HoursMinutes = "{0} h {1} min",
    };

    private static readonly TransferTextFormats French = new()
    {
        Progress = "{0} %, {1:0.0} Mo/s, {2}",
        ProgressWithoutRate = "{0} %",
        Remaining = "{0} restantes",
        Seconds = "{0} s",
        Minutes = "{0} min",
        MinutesSeconds = "{0} min {1} s",
        HoursMinutes = "{0} h {1} min",
    };

    private static readonly CultureInfo UnitedStates = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo France = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>
    /// 42 % of 450 MB done, 2.6 MB/s over the last six seconds, so 100 seconds left.
    /// </summary>
    private static TransferProgress Running()
    {
        var progress = new TransferProgress(450_000_000, 0);
        progress.Report(10_000, 174_400_000);
        progress.Report(16_000, 190_000_000);
        return progress;
    }

    [Fact]
    public void The_progress_gives_the_percentage_the_rate_and_the_time_left()
    {
        Assert.Equal("42 %, 2.6 MB/s, 1 min 40 s left", TransferProgressText.Describe(Running(), English, UnitedStates));
    }

    [Fact]
    public void The_numbers_follow_the_culture()
    {
        Assert.Equal("42 %, 2,6 Mo/s, 1 min 40 s restantes", TransferProgressText.Describe(Running(), French, France));
    }

    [Fact]
    public void Without_a_rate_only_the_percentage_is_given()
    {
        var progress = new TransferProgress(1000, 0);
        progress.Report(100, 429);

        Assert.Equal("42 %", TransferProgressText.Describe(progress, English, UnitedStates));
    }

    [Theory]
    [InlineData(0, "5 s left")]
    [InlineData(41, "45 s left")]
    [InlineData(55, "55 s left")]
    [InlineData(56, "1 min left")]
    [InlineData(60, "1 min left")]
    [InlineData(61, "1 min 5 s left")]
    [InlineData(100, "1 min 40 s left")]
    [InlineData(3594, "59 min 55 s left")]
    [InlineData(3596, "1 h 0 min left")]
    [InlineData(3661, "1 h 2 min left")]
    [InlineData(7500, "2 h 5 min left")]
    public void The_time_left_is_rounded_up_to_five_seconds_under_an_hour(int seconds, string expected)
    {
        Assert.Equal(expected, TransferProgressText.TimeLeftText(TimeSpan.FromSeconds(seconds), English, UnitedStates));
    }

    [Theory]
    [InlineData(-3, TimeLeftUnit.Seconds, 0, 0, 5)]
    [InlineData(12, TimeLeftUnit.Seconds, 0, 0, 15)]
    [InlineData(125, TimeLeftUnit.Minutes, 0, 2, 5)]
    [InlineData(126, TimeLeftUnit.Minutes, 0, 2, 10)]
    [InlineData(3600, TimeLeftUnit.Hours, 1, 0, 0)]
    public void The_detailed_time_left_keeps_the_seconds(int seconds, TimeLeftUnit unit, int hours, int minutes, int roundedSeconds)
    {
        Assert.Equal(new TimeLeft(unit, hours, minutes, roundedSeconds), TimeLeft.Detailed(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0.2, "1 s")]
    [InlineData(16.4, "16 s")]
    [InlineData(59.6, "1 min")]
    [InlineData(193.4, "3 min 13 s")]
    [InlineData(3599.4, "59 min 59 s")]
    [InlineData(7322, "2 h 2 min")]
    [InlineData(7350, "2 h 3 min")]
    public void A_duration_is_rounded_to_the_second(double seconds, string expected)
    {
        Assert.Equal(expected, TransferProgressText.Duration(TimeSpan.FromSeconds(seconds), English, UnitedStates));
    }

    [Fact]
    public void The_rate_of_a_finished_transfer_is_in_megabytes_per_second()
    {
        Assert.Equal(2.57, TransferProgressText.MegabytesPerSecond(496_000_000, 193), 2);
        Assert.Equal(0, TransferProgressText.MegabytesPerSecond(496_000_000, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(99, 0)]
    [InlineData(100, 2)]
    [InlineData(149, 2)]
    [InlineData(150, 3)]
    [InlineData(400, 3)]
    public void The_current_file_is_the_first_one_not_complete(long doneBytes, int expected)
    {
        // The second file is empty: it is complete as soon as the first one is.
        Assert.Equal(expected, TransferProgress.CurrentFile([100, 0, 50, 250], doneBytes));
    }

    [Fact]
    public void Without_files_there_is_no_current_file()
    {
        Assert.Equal(-1, TransferProgress.CurrentFile([], 0));
    }
}
