using RdpDrop.Core.Recent;

namespace RdpDrop.Core.Tests.Recent;

public class RecentListTests
{
    private static List<string> Paths(int count) =>
        Enumerable.Range(1, count).Select(i => $@"C:\Sessions\server{i}.rdp").ToList();

    [Fact]
    public void Add_to_an_empty_list_gives_one_entry()
    {
        Assert.Equal([@"C:\Sessions\a.rdp"], RecentList.Add([], @"C:\Sessions\a.rdp"));
    }

    [Fact]
    public void Add_to_a_null_list_gives_one_entry()
    {
        Assert.Equal([@"C:\Sessions\a.rdp"], RecentList.Add(null, @"C:\Sessions\a.rdp"));
    }

    [Fact]
    public void Add_puts_the_new_path_first()
    {
        var result = RecentList.Add([@"C:\a.rdp", @"C:\b.rdp"], @"C:\c.rdp");

        Assert.Equal([@"C:\c.rdp", @"C:\a.rdp", @"C:\b.rdp"], result);
    }

    [Fact]
    public void Add_of_a_known_path_moves_it_to_the_top()
    {
        var result = RecentList.Add([@"C:\a.rdp", @"C:\b.rdp", @"C:\c.rdp"], @"C:\c.rdp");

        Assert.Equal([@"C:\c.rdp", @"C:\a.rdp", @"C:\b.rdp"], result);
    }

    [Fact]
    public void Add_ignores_case_and_keeps_the_new_spelling()
    {
        var result = RecentList.Add([@"C:\Sessions\A.rdp", @"C:\b.rdp"], @"c:\sessions\a.RDP");

        Assert.Equal([@"c:\sessions\a.RDP", @"C:\b.rdp"], result);
    }

    [Fact]
    public void Add_trims_the_path()
    {
        Assert.Equal([@"C:\a.rdp"], RecentList.Add([@"C:\a.rdp"], @"  C:\a.rdp "));
    }

    [Fact]
    public void Add_keeps_ten_entries_and_drops_the_oldest()
    {
        var current = Paths(10);

        var result = RecentList.Add(current, @"C:\new.rdp");

        Assert.Equal(RecentList.MaxCount, result.Count);
        Assert.Equal(@"C:\new.rdp", result[0]);
        Assert.Equal(current.Take(9), result.Skip(1));
    }

    [Fact]
    public void Add_of_a_known_path_in_a_full_list_keeps_every_other_entry()
    {
        var current = Paths(10);

        var result = RecentList.Add(current, current[9]);

        Assert.Equal(10, result.Count);
        Assert.Equal(current[9], result[0]);
        Assert.Equal(current.Take(9), result.Skip(1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_refuses_a_blank_path(string path)
    {
        Assert.Throws<ArgumentException>(() => RecentList.Add([], path));
    }

    [Fact]
    public void Add_does_not_change_its_input()
    {
        var current = new List<string> { @"C:\a.rdp" };

        RecentList.Add(current, @"C:\b.rdp");

        Assert.Equal([@"C:\a.rdp"], current);
    }

    [Fact]
    public void Remove_takes_out_the_path_whatever_its_case()
    {
        var result = RecentList.Remove([@"C:\a.rdp", @"C:\B.rdp", @"C:\c.rdp"], @"c:\b.RDP");

        Assert.Equal([@"C:\a.rdp", @"C:\c.rdp"], result);
    }

    [Fact]
    public void Remove_of_an_unknown_path_keeps_the_list()
    {
        Assert.Equal([@"C:\a.rdp"], RecentList.Remove([@"C:\a.rdp"], @"C:\z.rdp"));
    }

    [Fact]
    public void Remove_of_null_keeps_the_list()
    {
        Assert.Equal([@"C:\a.rdp"], RecentList.Remove([@"C:\a.rdp"], null));
    }

    [Fact]
    public void Normalize_drops_blanks_and_repeats()
    {
        var result = RecentList.Normalize([@"C:\a.rdp", null, "", "  ", @"C:\A.RDP", @"C:\b.rdp", @"C:\b.rdp"]);

        Assert.Equal([@"C:\a.rdp", @"C:\b.rdp"], result);
    }

    [Fact]
    public void Normalize_trims_a_long_list_to_ten()
    {
        var current = Paths(15);

        Assert.Equal(current.Take(10), RecentList.Normalize(current));
    }

    [Fact]
    public void Normalize_of_null_is_empty()
    {
        Assert.Empty(RecentList.Normalize(null));
    }
}
