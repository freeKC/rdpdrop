using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Core.Tests.RemoteApp;

/// <summary>The rules of the client for RemoteApp sessions added in 0.9.0.</summary>
public class RemoteAppClientRulesTests
{
    [Theory]
    [InlineData("RAIL_WINDOW", 0x00080100L, "Untitled - Notepad", RailWindowKind.Program)]
    [InlineData("RAIL_WINDOW", 0x08080088L, "UltimateRDP drag", RailWindowKind.DropProxy)]
    [InlineData("RAIL_WINDOW", 0x08080088L, "", RailWindowKind.NotActivatable)]
    [InlineData("RAIL_WINDOW", 0x00000080L, "Find", RailWindowKind.Program)]
    [InlineData("UIMainClass", 0L, "server.example.com", RailWindowKind.Control)]
    [InlineData("uimainclass", 0L, null, RailWindowKind.Control)]
    [InlineData("TscShellContainerClass", 0L, null, RailWindowKind.Control)]
    [InlineData("WindowsForms10.Window.8.app.0.1", 0L, "UltimateRDP", RailWindowKind.OtherClass)]
    [InlineData(null, 0L, null, RailWindowKind.OtherClass)]
    public void Only_the_windows_of_the_program_count(string? className, long exStyle, string? title, RailWindowKind expected)
    {
        Assert.Equal(expected, RailWindowRule.Classify(className, exStyle, title));
        Assert.Equal(expected == RailWindowKind.Program, RailWindowRule.IsProgramWindow(className, exStyle, title));
    }

    [Theory]
    [InlineData(2, 11u, true)]
    [InlineData(2, 12u, true)]
    [InlineData(1, 0u, true)]
    [InlineData(3, 1u, true)]
    [InlineData(3, 2u, true)]
    [InlineData(3, 3u, false)]
    [InlineData(2, 5u, false)]
    [InlineData(264, 0u, false)]
    [InlineData(0, 11u, false)]
    public void The_ends_that_a_user_or_Windows_cause_on_purpose_are_normal(int reason, uint extended, bool normal)
    {
        Assert.Equal(normal, SessionEnd.IsNormal(reason, extended));
    }

    [Fact]
    public void The_extended_reason_of_the_end_of_a_RemoteApp_session_is_described()
    {
        Assert.Contains("RemoteApp", SessionEnd.DescribeExtended(11), StringComparison.Ordinal);
        Assert.Equal("extended reason 77", SessionEnd.DescribeExtended(77));
    }

    [Fact]
    public void ServerStartProgram_is_called_at_the_logon_and_once_more_when_nothing_came()
    {
        var start = new RemoteProgramStart();

        Assert.Equal(RemoteProgramStep.CallServerStartProgram, start.OnLoggedOn());
        Assert.Equal(RemoteProgramStep.None, start.OnLoggedOn());
        Assert.Equal(RemoteProgramStep.CallServerStartProgram, start.OnRetryTimer());
        Assert.Equal(RemoteProgramStep.None, start.OnRetryTimer());
        Assert.Equal(2, start.Calls);
    }

    [Fact]
    public void A_result_or_a_window_before_the_logon_means_no_call()
    {
        var answered = new RemoteProgramStart();
        answered.OnResult();
        var shown = new RemoteProgramStart();
        shown.OnWindow();

        Assert.Equal(RemoteProgramStep.None, answered.OnLoggedOn());
        Assert.Equal(RemoteProgramStep.None, shown.OnLoggedOn());
        Assert.Equal(RemoteProgramStep.None, shown.OnRetryTimer());
    }

    [Fact]
    public void A_result_after_the_first_call_stops_the_retry_and_a_new_connection_starts_again()
    {
        var start = new RemoteProgramStart();
        start.OnLoggedOn();
        start.OnResult();

        Assert.Equal(RemoteProgramStep.None, start.OnRetryTimer());

        start.Reset();
        Assert.Equal(RemoteProgramStep.None, start.OnRetryTimer());
        Assert.Equal(RemoteProgramStep.CallServerStartProgram, start.OnLoggedOn());
    }

    [Theory]
    [InlineData(new string[0], null, LaunchWindow.Auto, false)]
    [InlineData(new[] { @"C:\rdp\notepad.rdp" }, @"C:\rdp\notepad.rdp", LaunchWindow.Auto, false)]
    [InlineData(new[] { "--hidden", "app.rdp" }, "app.rdp", LaunchWindow.Hidden, false)]
    [InlineData(new[] { "app.rdp", "--SHOW", "--ask-credentials" }, "app.rdp", LaunchWindow.Show, true)]
    [InlineData(new[] { "--show", "--show" }, null, LaunchWindow.Show, false)]
    public void The_command_line_gives_the_file_and_the_options_in_any_order(string[] args, string? path, LaunchWindow window, bool ask)
    {
        var options = LaunchOptions.Parse(args);

        Assert.Null(options.Error);
        Assert.Equal(path, options.Path);
        Assert.Equal(window, options.Window);
        Assert.Equal(ask, options.AskCredentials);
    }

    [Theory]
    [InlineData("--show", "--hidden")]
    [InlineData("a.rdp", "b.rdp")]
    [InlineData("--minimized", "a.rdp")]
    public void A_wrong_command_line_is_refused(string first, string second)
    {
        Assert.NotNull(LaunchOptions.Parse([first, second]).Error);
    }

    [Theory]
    [InlineData(LaunchWindow.Auto, true, true)]
    [InlineData(LaunchWindow.Auto, false, false)]
    [InlineData(LaunchWindow.Show, true, false)]
    [InlineData(LaunchWindow.Hidden, false, true)]
    public void A_RemoteApp_program_starts_hidden_unless_the_command_line_says_otherwise(LaunchWindow window, bool remoteApp, bool hidden)
    {
        Assert.Equal(hidden, new LaunchOptions("a.rdp", window, false, null).StartsHidden(remoteApp));
    }
}
