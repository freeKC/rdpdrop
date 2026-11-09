namespace UltimateRDP.Core.Credentials;

/// <summary>
/// Splits the user name of a saved Windows credential into the user name and domain that
/// the Remote Desktop control takes.
/// </summary>
public static class AccountName
{
    /// <summary>
    /// Splits <paramref name="account"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>DOMAIN\user</c> gives <c>user</c> and <c>DOMAIN</c>; <c>.\user</c> gives <c>user</c> and <c>.</c>.</item>
    /// <item>
    /// <c>user@domain</c> is kept whole with an empty domain: a user principal name
    /// authenticates as it is, and its suffix is not always the name of the domain.
    /// </item>
    /// <item>Anything else is a user name with an empty domain.</item>
    /// </list>
    /// Spaces around the parts are removed. A null or blank account gives two empty strings.
    /// </remarks>
    public static (string UserName, string Domain) Split(string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return (string.Empty, string.Empty);
        }

        var text = account.Trim();
        var slash = text.IndexOf('\\');
        if (slash > 0 && slash < text.Length - 1)
        {
            return (text[(slash + 1)..].Trim(), text[..slash].Trim());
        }

        return (text, string.Empty);
    }
}
