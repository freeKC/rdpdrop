using UltimateRDP.Core.Agent;

namespace UltimateRDP.Core.Tests.Agent;

public class RemoteAppSessionRuleTests
{
    private const string Agent = "UltimateRDP.Agent.exe";

    // The processes found in every RemoteApp session of a production server, with the agent.
    private static readonly string[] RemoteAppBase =
    [
        "csrss.exe", "winlogon.exe", "fontdrvhost.exe", "dwm.exe", "rdpinit.exe", "rdpshell.exe", "sihost.exe",
        "svchost.exe", "svchost.exe", "taskhostw.exe", "ctfmon.exe", "rdpclip.exe", "TabTip.exe", "TabTip32.exe",
        "DefenderSessionHelper.exe", Agent,
    ];

    private static readonly string[] DefaultSysProcs = ["ctfmon.exe", "dwm.exe", "rdpclip.exe", "rdpinput.exe", "TaskEng.exe", "Taskhost.exe", "Taskhostex.exe"];

    private static string[] With(params string[] more) => [.. RemoteAppBase, .. more];

    [Fact]
    public void Explorer_makes_a_desktop_session()
    {
        Assert.Equal(SessionShell.Desktop, RemoteAppSessionRule.Classify(["csrss.exe", "winlogon.exe", "explorer.exe", "sihost.exe"]));
    }

    [Fact]
    public void Explorer_wins_over_a_remoteapp_shell()
    {
        Assert.Equal(SessionShell.Desktop, RemoteAppSessionRule.Classify(["rdpshell.exe", "EXPLORER.EXE"]));
    }

    [Theory]
    [InlineData("rdpshell.exe")]
    [InlineData("rdpinit.exe")]
    [InlineData("RDPSHELL.EXE")]
    [InlineData(@"C:\Windows\System32\rdpinit.exe")]
    public void The_remoteapp_shell_without_explorer_makes_a_remoteapp_session(string shell)
    {
        Assert.Equal(SessionShell.RemoteApp, RemoteAppSessionRule.Classify(["csrss.exe", shell, "MyApp.exe"]));
    }

    [Fact]
    public void Neither_shell_leaves_the_session_unknown()
    {
        Assert.Equal(SessionShell.Unknown, RemoteAppSessionRule.Classify(["csrss.exe", "winlogon.exe", Agent]));
        Assert.Equal(SessionShell.Unknown, RemoteAppSessionRule.Classify([]));
    }

    [Fact]
    public void The_published_program_is_an_application()
    {
        Assert.Equal(["MyApp.exe"], RemoteAppSessionRule.Applications(With("MyApp.exe"), DefaultSysProcs, Agent));
    }

    [Fact]
    public void Without_the_program_nothing_is_left()
    {
        Assert.Empty(RemoteAppSessionRule.Applications(RemoteAppBase, DefaultSysProcs, Agent));
    }

    [Fact]
    public void The_fixed_list_holds_without_any_sysprocs()
    {
        Assert.Empty(RemoteAppSessionRule.Applications(With("LogonUI.exe", "RuntimeBroker.exe", "SearchApp.exe", "ShellExperienceHost.exe", "conhost.exe"), [], Agent));
    }

    [Fact]
    public void A_helper_of_the_program_that_is_not_listed_keeps_the_session_open()
    {
        // As the server does: a process it does not know of counts, whoever started it.
        Assert.Equal(["splwow64.exe"], RemoteAppSessionRule.Applications(With("splwow64.exe"), DefaultSysProcs, Agent));
    }

    [Fact]
    public void A_sysprocs_name_is_ignored_in_any_case_and_without_its_extension()
    {
        string[] sysProcs = [.. DefaultSysProcs, "ADOBECOLLABSYNC.EXE", "helper"];

        Assert.Empty(RemoteAppSessionRule.Applications(With("AdobeCollabSync.exe", "Helper.exe"), sysProcs, Agent));
    }

    [Fact]
    public void The_agent_is_ignored_whatever_its_case()
    {
        Assert.Empty(RemoteAppSessionRule.Applications(["ultimaterdp.agent.EXE", "rdpshell.exe"], [], Agent));
    }

    [Fact]
    public void Each_application_comes_once_in_the_order_found()
    {
        Assert.Equal(["MyApp.exe", "Other.exe"], RemoteAppSessionRule.Applications(With("MyApp.exe", "Other.exe", "myapp.exe"), [], Agent));
    }

    [Theory]
    [InlineData("MyApp.exe", "myapp")]
    [InlineData(" MyApp.EXE ", "myapp")]
    [InlineData(@"C:\Apps\MyApp.exe", "myapp")]
    [InlineData("tool", "tool")]
    [InlineData("archive.exe.bak", "archive.exe.bak")]
    [InlineData(null, "")]
    public void Names_are_compared_without_folder_extension_or_case(string? name, string expected)
    {
        Assert.Equal(expected, RemoteAppSessionRule.Normalize(name));
    }

    [Fact]
    public void Two_empty_checks_make_the_agent_exit()
    {
        var watch = new RemoteAppExitWatch();

        var first = watch.Check([], busy: false);
        var second = watch.Check([], busy: false);

        Assert.Equal(RemoteAppVerdict.Stay, first.Verdict);
        Assert.Equal(1, first.EmptyChecks);
        Assert.Equal(RemoteAppVerdict.Exit, second.Verdict);
        Assert.Equal(2, second.EmptyChecks);
    }

    [Fact]
    public void An_application_between_two_empty_checks_starts_the_count_again()
    {
        var watch = new RemoteAppExitWatch();

        watch.Check([], busy: false);
        Assert.Equal(RemoteAppVerdict.Stay, watch.Check(["MyApp.exe"], busy: false).Verdict);
        Assert.Equal(RemoteAppVerdict.Stay, watch.Check([], busy: false).Verdict);
        Assert.Equal(RemoteAppVerdict.Exit, watch.Check([], busy: false).Verdict);
    }

    [Fact]
    public void A_drag_in_progress_holds_the_exit_until_it_ends()
    {
        var watch = new RemoteAppExitWatch();

        watch.Check([], busy: true);
        Assert.Equal(RemoteAppVerdict.StayWhileBusy, watch.Check([], busy: true).Verdict);
        Assert.Equal(RemoteAppVerdict.StayWhileBusy, watch.Check([], busy: true).Verdict);
        Assert.Equal(RemoteAppVerdict.Exit, watch.Check([], busy: false).Verdict);
    }

    [Fact]
    public void A_running_program_never_makes_the_agent_exit()
    {
        var watch = new RemoteAppExitWatch();

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(RemoteAppVerdict.Stay, watch.Check(["MyApp.exe"], busy: false).Verdict);
        }
    }

    [Fact]
    public void A_disconnection_without_application_makes_the_agent_exit_at_once()
    {
        var watch = new RemoteAppExitWatch();

        Assert.Equal(RemoteAppVerdict.Exit, watch.OnDisconnected([]).Verdict);
    }

    [Fact]
    public void A_disconnection_with_the_program_running_keeps_the_agent()
    {
        var watch = new RemoteAppExitWatch();

        Assert.Equal(RemoteAppVerdict.Stay, watch.OnDisconnected(["MyApp.exe"]).Verdict);
        Assert.Equal(RemoteAppVerdict.Stay, watch.Check(["MyApp.exe"], busy: false).Verdict);
    }

    [Fact]
    public void Only_a_change_asks_for_a_log_line()
    {
        var watch = new RemoteAppExitWatch();

        Assert.True(watch.Check(["MyApp.exe"], busy: false).Changed);
        Assert.False(watch.Check(["MyApp.exe"], busy: false).Changed);
        Assert.False(watch.Check(["myapp.exe"], busy: false).Changed);
        Assert.True(watch.Check(["MyApp.exe", "Other.exe"], busy: false).Changed);
        Assert.True(watch.Check([], busy: false).Changed);
        Assert.True(watch.Check([], busy: false).Changed);
    }

    [Fact]
    public void The_descriptions_name_the_state()
    {
        var watch = new RemoteAppExitWatch();

        Assert.Equal("applications running: MyApp.exe", watch.Check(["MyApp.exe"], busy: false).Describe());
        Assert.Equal("no application is left at check 1 of 2", watch.Check([], busy: false).Describe());
        Assert.Equal("no application is left after 2 check(s), but a drag or a transfer runs", watch.Check([], busy: true).Describe());
        Assert.Equal("no application is left after 3 check(s)", watch.Check([], busy: false).Describe());
    }

    [Fact]
    public void The_check_interval_and_the_count_outlast_the_grace_of_the_server()
    {
        // The server waits 20 seconds after the last window before it disconnects the session.
        Assert.True(RemoteAppSessionRule.CheckInterval * RemoteAppSessionRule.EmptyChecksToExit > TimeSpan.FromSeconds(20));
    }
}
