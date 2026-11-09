using UltimateRDP.Core.Credentials;

namespace UltimateRDP.Core.Tests.Credentials;

public class AccountNameTests
{
    [Fact]
    public void Domain_and_user_are_split_at_the_backslash()
    {
        Assert.Equal(("alice", "CONTOSO"), AccountName.Split(@"CONTOSO\alice"));
    }

    [Fact]
    public void Local_account_keeps_the_dot_as_domain()
    {
        Assert.Equal(("admin", "."), AccountName.Split(@".\admin"));
    }

    [Fact]
    public void User_principal_name_is_kept_whole()
    {
        Assert.Equal(("alice@contoso.com", string.Empty), AccountName.Split("alice@contoso.com"));
    }

    [Fact]
    public void Plain_user_name_has_no_domain()
    {
        Assert.Equal(("alice", string.Empty), AccountName.Split("alice"));
    }

    [Fact]
    public void Only_the_first_backslash_splits()
    {
        Assert.Equal((@"alice\x", "CONTOSO"), AccountName.Split(@"CONTOSO\alice\x"));
    }

    [Theory]
    [InlineData(@"\alice")]
    [InlineData(@"CONTOSO\")]
    public void Backslash_at_an_end_does_not_split(string account)
    {
        Assert.Equal((account, string.Empty), AccountName.Split(account));
    }

    [Fact]
    public void Spaces_around_the_parts_are_removed()
    {
        Assert.Equal(("alice", "CONTOSO"), AccountName.Split(@"  CONTOSO \ alice "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_account_gives_empty_parts(string? account)
    {
        Assert.Equal((string.Empty, string.Empty), AccountName.Split(account));
    }
}
