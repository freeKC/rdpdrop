using System.Text;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Core.Tests.RemoteApp;

/// <summary>The steps and the rules of the loader of a start, since 0.9.3.</summary>
public class LoaderTests
{
    [Fact]
    public void A_RemoteApp_start_goes_through_every_step_and_closes_at_the_first_window()
    {
        var progress = new LoaderProgress(0);
        Assert.Equal(LoaderStep.Starting, progress.Step);
        Assert.Equal("LoaderStarting", progress.StepTextKey);
        Assert.Null(progress.Title);

        progress.FileRead("server.example.com", "Notepad");
        Assert.Equal("Notepad", progress.Title);
        Assert.True(progress.IsRemoteApp);

        Assert.True(progress.On(LoaderEvent.Connecting));
        Assert.Equal(("LoaderConnecting", "server.example.com"), (progress.StepTextKey, progress.StepTextArgument));
        Assert.True(progress.On(LoaderEvent.Connected));
        Assert.Equal(("LoaderOpeningSession", (string?)null), (progress.StepTextKey, progress.StepTextArgument));
        Assert.True(progress.On(LoaderEvent.LoggedOn));
        Assert.Equal(("LoaderStartingProgram", "Notepad"), (progress.StepTextKey, progress.StepTextArgument));
        Assert.False(progress.On(LoaderEvent.LoggedOn));
        Assert.True(progress.On(LoaderEvent.FirstProgramWindow));
        Assert.True(progress.IsClosed);
        Assert.Equal("the first window of the program appeared", progress.CloseReason);
        Assert.False(progress.On(LoaderEvent.Ended));
    }

    [Fact]
    public void A_desktop_start_closes_at_the_logon_and_ignores_program_windows()
    {
        var progress = new LoaderProgress(0);
        progress.FileRead("server.example.com", null);

        Assert.Equal("server.example.com", progress.Title);
        Assert.False(progress.IsRemoteApp);
        progress.On(LoaderEvent.Connecting);
        Assert.False(progress.On(LoaderEvent.FirstProgramWindow));
        progress.On(LoaderEvent.Connected);
        Assert.True(progress.On(LoaderEvent.LoggedOn));
        Assert.True(progress.IsClosed);
        Assert.Equal("the desktop is logged on", progress.CloseReason);
    }

    [Theory]
    [InlineData(LoaderStep.Starting)]
    [InlineData(LoaderStep.Connecting)]
    [InlineData(LoaderStep.OpeningSession)]
    [InlineData(LoaderStep.StartingProgram)]
    public void Any_end_before_the_start_is_over_closes_the_loader(LoaderStep reached)
    {
        var progress = new LoaderProgress(0);
        progress.FileRead("server.example.com", "Notepad");
        LoaderEvent[] events = [LoaderEvent.Connecting, LoaderEvent.Connected, LoaderEvent.LoggedOn];
        foreach (var loaderEvent in events.Take((int)reached))
        {
            progress.On(loaderEvent);
        }

        Assert.Equal(reached, progress.Step);
        Assert.True(progress.On(LoaderEvent.Ended));
        Assert.True(progress.IsClosed);
        Assert.Equal("the start ended before it was over", progress.CloseReason);
    }

    [Fact]
    public void Steps_never_go_back()
    {
        var progress = new LoaderProgress(0);
        progress.FileRead("server.example.com", "Notepad");
        progress.On(LoaderEvent.Connected);

        Assert.False(progress.On(LoaderEvent.Connecting));
        Assert.Equal(LoaderStep.OpeningSession, progress.Step);
    }

    [Fact]
    public void The_slow_notice_is_due_ten_seconds_after_the_loader_appeared_and_not_once_it_closed()
    {
        var progress = new LoaderProgress(5_000);

        Assert.False(progress.ShowsSlowNotice(14_999));
        Assert.True(progress.ShowsSlowNotice(15_000));
        progress.On(LoaderEvent.Ended);
        Assert.False(progress.ShowsSlowNotice(60_000));
    }

    [Theory]
    [InlineData("#32770", true, true)]
    [InlineData("#32770", false, false)]
    [InlineData("Credential Dialog Xaml Host", false, true)]
    [InlineData("Credential Dialog Xaml Host", true, true)]
    [InlineData("RAIL_WINDOW", true, false)]
    [InlineData(null, true, false)]
    public void Only_a_dialog_of_this_process_or_the_credential_prompt_awaits_an_answer(string? className, bool ofThisProcess, bool awaited)
    {
        Assert.Equal(awaited, LoaderRules.IsAnswerAwaited(className, ofThisProcess));
    }

    [Theory]
    [InlineData("""{"loader":"off","language":"fr"}""", false, "fr")]
    [InlineData("""{ "recentFiles": ["a.rdp", {"x": 1}], "Loader": "OFF" }""", false, null)]
    [InlineData("""// comment\n{"language":"de", "loader": "on",}""", true, "de")]
    [InlineData("""{"loader":"maybe"}""", true, null)]
    [InlineData("""{"loader":false}""", true, null)]
    [InlineData("""not json""", true, null)]
    [InlineData("", true, null)]
    public void The_early_settings_read_the_loader_and_the_language(string json, bool loader, string? language)
    {
        Assert.Equal((loader, language), LoaderRules.ReadEarlySettings(Encoding.UTF8.GetBytes(json.Replace("\\n", "\n", StringComparison.Ordinal))));
    }

    [Fact]
    public void The_early_settings_skip_a_byte_order_mark()
    {
        byte[] json = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("""{"loader":"off"}""")];

        Assert.False(LoaderRules.ReadEarlySettings(json).Loader);
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData("on", true, true)]
    [InlineData(" Off ", false, true)]
    [InlineData("auto", true, false)]
    public void The_loader_setting_reads_on_or_off(string? value, bool loader, bool recognized)
    {
        Assert.Equal(loader, LoaderRules.ParseLoader(value, out var known));
        Assert.Equal(recognized, known);
        Assert.Equal(loader ? "on" : "off", LoaderRules.ToSetting(loader));
    }
}
