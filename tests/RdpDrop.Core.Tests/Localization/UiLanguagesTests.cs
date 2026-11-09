using RdpDrop.Core.Localization;

namespace RdpDrop.Core.Tests.Localization;

public class UiLanguagesTests
{
    [Fact]
    public void Codes_list_the_seven_languages_english_first()
    {
        Assert.Equal(["en", "fr", "de", "es", "it", "pt", "nl"], UiLanguages.Codes);
        Assert.Equal("auto", UiLanguages.Automatic);
        Assert.Equal("en", UiLanguages.English);
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("FR", true)]
    [InlineData(" nl ", true)]
    [InlineData("pt", true)]
    [InlineData("auto", false)]
    [InlineData("ja", false)]
    [InlineData("fr-FR", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Supported_codes_are_recognized(string? code, bool expected)
    {
        Assert.Equal(expected, UiLanguages.IsSupported(code));
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData("fr", "fr")]
    [InlineData(" De ", "de")]
    [InlineData("es", "es")]
    [InlineData("it", "it")]
    [InlineData("pt", "pt")]
    [InlineData("nl", "nl")]
    [InlineData("en", "en")]
    [InlineData("ja", "auto")]
    [InlineData("fr-CA", "auto")]
    [InlineData("", "auto")]
    [InlineData("   ", "auto")]
    [InlineData(null, "auto")]
    public void Choices_are_normalized(string? choice, string expected)
    {
        Assert.Equal(expected, UiLanguages.NormalizeChoice(choice));
    }

    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("DE", "de")]
    [InlineData("nl", "nl")]
    [InlineData("pt", "pt")]
    [InlineData("en", "en")]
    [InlineData("ja", "en")]
    [InlineData("zh", "en")]
    [InlineData("iv", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void Windows_languages_are_matched(string? twoLetter, string expected)
    {
        Assert.Equal(expected, UiLanguages.Match(twoLetter));
    }

    [Theory]
    [InlineData("auto", "fr", "fr")]
    [InlineData("auto", "ja", "en")]
    [InlineData(null, "es", "es")]
    [InlineData("bogus", "it", "it")]
    [InlineData("de", "fr", "de")]
    [InlineData("en", "fr", "en")]
    [InlineData("nl", null, "nl")]
    [InlineData("auto", null, "en")]
    public void Choice_wins_over_the_windows_language(string? choice, string? windows, string expected)
    {
        Assert.Equal(expected, UiLanguages.Resolve(choice, windows));
    }
}
