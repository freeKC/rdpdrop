using UltimateRDP.Core.Credentials;
using UltimateRDP.Core.RdpFiles;

namespace UltimateRDP.Core.Tests.Credentials;

/// <summary>Whether the client asks for the password, since 0.9.0.</summary>
public class CredentialPromptTests
{
    [Theory]
    [InlineData(false, false, false, false, CredentialPromptSource.None)]
    [InlineData(true, false, false, true, CredentialPromptSource.CommandLine)]
    [InlineData(false, true, false, true, CredentialPromptSource.Setting)]
    [InlineData(false, false, true, true, CredentialPromptSource.RdpFile)]
    [InlineData(true, true, true, true, CredentialPromptSource.CommandLine)]
    [InlineData(false, true, true, true, CredentialPromptSource.Setting)]
    public void Any_of_the_three_inputs_asks_and_the_first_one_is_named(bool commandLine, bool setting, bool file, bool ask, CredentialPromptSource source)
    {
        var decision = CredentialPrompt.Decide(commandLine, setting, file);

        Assert.Equal(ask, decision.Ask);
        Assert.Equal(source, decision.Source);
        Assert.False(string.IsNullOrWhiteSpace(decision.Describe()));
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData("", false, true)]
    [InlineData("saved", false, true)]
    [InlineData(" Ask ", true, true)]
    [InlineData("always", false, false)]
    public void The_setting_reads_saved_or_ask(string? value, bool ask, bool recognized)
    {
        Assert.Equal(ask, CredentialPrompt.ParseSetting(value, out var known));
        Assert.Equal(recognized, known);
    }

    [Fact]
    public void The_setting_writes_saved_or_ask()
    {
        Assert.Equal("ask", CredentialPrompt.ToSetting(true));
        Assert.Equal("saved", CredentialPrompt.ToSetting(false));
    }

    [Theory]
    [InlineData("prompt for credentials:i:1", true)]
    [InlineData("prompt for credentials:i:0", false)]
    [InlineData("username:s:DOMAIN\\user", false)]
    public void The_key_of_the_rdp_file_asks_when_it_is_1(string line, bool prompts)
    {
        var file = RdpFile.Parse("full address:s:server.example.com\r\n" + line + "\r\n");

        Assert.Equal(prompts, CredentialPrompt.Decide(false, false, file.PromptForCredentials).Ask);
    }
}
