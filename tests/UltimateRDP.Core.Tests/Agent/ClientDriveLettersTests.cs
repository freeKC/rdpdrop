using UltimateRDP.Core.Agent;

namespace UltimateRDP.Core.Tests.Agent;

public class ClientDriveLettersTests
{
    private static IReadOnlyList<ClientDriveLetter> Choose(string drives, string taken, out string unmapped)
    {
        var result = ClientDriveLetters.Choose(drives, taken, out var missing);
        unmapped = new string([.. missing]);
        return result;
    }

    private static string Describe(IReadOnlyList<ClientDriveLetter> letters) =>
        string.Join(" ", letters.Select(letter => $"{letter.ClientDrive}{letter.Letter}"));

    [Fact]
    public void Free_letters_keep_the_letter_of_the_client()
    {
        Assert.Equal("DD EE", Describe(Choose("DE", "C", out var unmapped)));
        Assert.Empty(unmapped);
    }

    [Fact]
    public void A_letter_in_use_sends_the_drive_to_the_first_free_letter_from_z()
    {
        // C is the system drive of the session, as almost always.
        Assert.Equal("CZ DD", Describe(Choose("CD", "C", out _)));
    }

    [Fact]
    public void A_moved_drive_never_takes_the_letter_of_a_later_drive()
    {
        // C and D are taken in the session; the client also has Z and Y.
        Assert.Equal("CX DW ZZ YY", Describe(Choose("CDZY", "CD", out _)));
    }

    [Fact]
    public void Letters_taken_high_in_the_alphabet_are_skipped()
    {
        Assert.Equal("CX", Describe(Choose("C", "CZY", out _)));
    }

    [Theory]
    [InlineData("AB")]
    [InlineData("ab")]
    [InlineData("1:")]
    [InlineData("")]
    public void A_b_and_what_is_not_a_letter_are_skipped(string drives)
    {
        Assert.Empty(Choose(drives, "C", out var unmapped));
        Assert.Empty(unmapped);
    }

    [Fact]
    public void A_and_b_are_never_given()
    {
        var letters = Choose("CDEFGHIJKLMNOPQRSTUVWXYZ", "CDEFGHIJKLMNOPQRSTUVWX", out var unmapped);

        Assert.Equal("YY ZZ", Describe(letters));
        Assert.Equal("CDEFGHIJKLMNOPQRSTUVWX", unmapped);
        Assert.DoesNotContain(letters, letter => letter.Letter is 'A' or 'B');
    }

    [Fact]
    public void More_drives_than_letters_leave_the_last_ones_without_a_letter()
    {
        // Eighteen letters of the session are in use; six are free for eight drives.
        var letters = Choose("CDEFGHIJ", "CDEFGHIJKLMNOPQRST", out var unmapped);

        Assert.Equal("CZ DY EX FW GV HU", Describe(letters));
        Assert.Equal("IJ", unmapped);
    }

    [Fact]
    public void Each_drive_and_each_letter_comes_once()
    {
        var letters = Choose("cCdD", "c", out _);

        Assert.Equal("CZ DD", Describe(letters));
        Assert.Equal(letters.Count, letters.Select(letter => letter.Letter).Distinct().Count());
    }

    [Fact]
    public void Lower_case_letters_in_use_count()
    {
        Assert.Equal("DZ", Describe(Choose("D", "cd", out _)));
    }

    [Fact]
    public void The_names_are_the_share_and_the_letter()
    {
        var letter = new ClientDriveLetter('C', 'Z');

        Assert.Equal(@"\\tsclient\C", letter.RemoteName);
        Assert.Equal("Z:", letter.LocalName);
    }

    [Theory]
    [InlineData("never", MapClientDrivesMode.Never)]
    [InlineData("auto", MapClientDrivesMode.Auto)]
    [InlineData(" AUTO ", MapClientDrivesMode.Auto)]
    [InlineData(null, MapClientDrivesMode.Never)]
    [InlineData("", MapClientDrivesMode.Never)]
    public void The_setting_is_read_in_any_case(string? value, MapClientDrivesMode expected)
    {
        Assert.Equal(expected, ClientDriveLetters.Parse(value, out var recognized));
        Assert.True(recognized);
    }

    [Theory]
    [InlineData("always")]
    [InlineData("yes")]
    public void An_unknown_setting_gives_never_and_is_reported(string value)
    {
        Assert.Equal(MapClientDrivesMode.Never, ClientDriveLetters.Parse(value, out var recognized));
        Assert.False(recognized);
    }

    [Theory]
    [InlineData(MapClientDrivesMode.Never, "never")]
    [InlineData(MapClientDrivesMode.Auto, "auto")]
    public void The_setting_round_trips(MapClientDrivesMode mode, string text)
    {
        Assert.Equal(text, ClientDriveLetters.ToSetting(mode));
        Assert.Equal(mode, ClientDriveLetters.Parse(text, out _));
    }
}
