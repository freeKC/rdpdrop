using UltimateRDP.Core.RdpFiles;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Core.Tests.RemoteApp;

/// <summary>RemoteApp launches that share one connection, since 0.10.0.</summary>
public class SharedConnectionTests
{
    [Theory]
    [InlineData("full address:s:server.example.com", "full address:s:SERVER.example.com", true)]
    [InlineData("full address:s:server.example.com", "full address:s:server.example.com:3389", true)]
    [InlineData("full address:s:server.example.com\nserver port:i:3389", "full address:s:server.example.com", true)]
    [InlineData("full address:s:server.example.com:3390", "full address:s:server.example.com", false)]
    [InlineData("full address:s:server.example.com", "full address:s:other.example.com", false)]
    [InlineData("full address:s:server.example.com\nusername:s:jdoe", "full address:s:server.example.com", true)]
    [InlineData("full address:s:server.example.com\nusername:s:jdoe", "full address:s:server.example.com\nusername:s:JDOE", true)]
    [InlineData("full address:s:server.example.com\nusername:s:EXAMPLE\\jdoe", "full address:s:server.example.com\nusername:s:jdoe\ndomain:s:example", true)]
    [InlineData("full address:s:server.example.com\nusername:s:jdoe\ndomain:s:EXAMPLE", "full address:s:server.example.com\nusername:s:jdoe", true)]
    [InlineData("full address:s:server.example.com\nusername:s:jdoe\ndomain:s:EXAMPLE", "full address:s:server.example.com\nusername:s:jdoe\ndomain:s:OTHER", false)]
    [InlineData("full address:s:server.example.com\nusername:s:jdoe", "full address:s:server.example.com\nusername:s:asmith", false)]
    [InlineData("full address:s:server.example.com", "full address:s:server.example.com\nusername:s:jdoe", false)]
    [InlineData("full address:s:server.example.com\ngatewayhostname:s:gw.example.com\ngatewayusagemethod:i:1", "full address:s:server.example.com", false)]
    [InlineData("full address:s:server.example.com\ngatewayhostname:s:gw.example.com\ngatewayusagemethod:i:1", "full address:s:server.example.com\ngatewayhostname:s:GW.example.com\ngatewayusagemethod:i:2", true)]
    [InlineData("full address:s:server.example.com\ngatewayhostname:s:gw.example.com\ngatewayusagemethod:i:0", "full address:s:server.example.com", true)]
    public void A_launch_joins_a_connection_with_the_same_target_and_account(string existing, string launch, bool matches)
    {
        var a = ConnectionTarget.FromRdpFile(RdpFile.Parse(existing));
        var b = ConnectionTarget.FromRdpFile(RdpFile.Parse(launch));

        Assert.Equal(matches, ConnectionTarget.Matches(a, b));
    }

    [Fact]
    public void A_launch_waits_for_the_logon_then_starts_after_the_first_program()
    {
        var connection = new SharedRemoteAppConnection<string>();

        Assert.Equal(JoinDecision.Queued, connection.Join("Calc"));
        Assert.Equal(JoinDecision.Queued, connection.Join("Paint"));
        Assert.Equal(["Calc", "Paint"], connection.OnLoggedOn());
        Assert.Empty(connection.Queued);
        Assert.Equal(JoinDecision.StartNow, connection.Join("Notepad"));
        Assert.Equal(["Calc", "Paint", "Notepad"], connection.Joined);
        Assert.Empty(connection.OnLoggedOn());
    }

    [Fact]
    public void A_launch_after_the_end_opens_its_own_connection()
    {
        var connection = new SharedRemoteAppConnection<string>();
        connection.OnLoggedOn();
        connection.OnEnded(closedByUser: false);

        Assert.Equal(JoinDecision.OpenOwn, connection.Join("Calc"));
        Assert.Empty(connection.Joined);
    }

    [Fact]
    public void At_the_end_the_waiting_and_unconfirmed_launches_open_their_own_connection()
    {
        var connection = new SharedRemoteAppConnection<string>();
        connection.Join("Calc");
        connection.OnLoggedOn();
        connection.Join("Paint");
        connection.Join("Notepad");
        connection.Confirm("Paint");

        Assert.Equal(["Calc", "Notepad"], connection.OnEnded(closedByUser: false));
        Assert.Empty(connection.OnEnded(closedByUser: false));
        Assert.Equal(SharedConnectionPhase.Ended, connection.Phase);
    }

    [Fact]
    public void A_connection_that_ends_before_the_logon_hands_its_queue_back()
    {
        var connection = new SharedRemoteAppConnection<string>();
        connection.Join("Calc");

        Assert.Equal(["Calc"], connection.OnEnded(closedByUser: false));
    }

    [Fact]
    public void A_connection_closed_by_the_user_drops_its_launches()
    {
        var connection = new SharedRemoteAppConnection<string>();
        connection.Join("Calc");

        Assert.Empty(connection.OnEnded(closedByUser: true));
    }

    [Fact]
    public void A_reconnection_takes_launches_again()
    {
        var connection = new SharedRemoteAppConnection<string>();
        connection.OnEnded(closedByUser: false);
        connection.OnConnecting();

        Assert.Equal(JoinDecision.Queued, connection.Join("Calc"));
    }

    [Fact]
    public void A_joined_start_ends_at_a_new_window()
    {
        var start = new JoinedProgramStart();

        Assert.False(start.OnWindows(5));
        start.OnStarted(windows: 1, nowMilliseconds: 1000);
        Assert.False(start.OnWindows(1));
        Assert.True(start.OnWindows(2));
        Assert.Equal(JoinedStartEnd.NewWindow, start.End);
        Assert.False(start.OnWindows(3));
    }

    [Fact]
    public void A_joined_start_ends_a_while_after_an_ok_without_a_new_window()
    {
        var start = new JoinedProgramStart();
        start.OnStarted(windows: 1, nowMilliseconds: 1000);

        Assert.False(start.OnResult(ok: true, nowMilliseconds: 1500));
        Assert.False(start.OnTick(1500 + JoinedProgramStart.OkGraceMilliseconds - 1));
        Assert.True(start.OnTick(1500 + JoinedProgramStart.OkGraceMilliseconds));
        Assert.Equal(JoinedStartEnd.AnsweredOk, start.End);
    }

    [Fact]
    public void A_joined_start_ends_at_a_failure_or_at_the_time_limit()
    {
        var failed = new JoinedProgramStart();
        failed.OnStarted(windows: 0, nowMilliseconds: 0);
        Assert.True(failed.OnResult(ok: false, nowMilliseconds: 10));
        Assert.Equal(JoinedStartEnd.Failed, failed.End);

        var silent = new JoinedProgramStart();
        silent.OnStarted(windows: 0, nowMilliseconds: 0);
        Assert.False(silent.OnTick(JoinedProgramStart.TimeoutMilliseconds - 1));
        Assert.True(silent.OnTick(JoinedProgramStart.TimeoutMilliseconds));
        Assert.Equal(JoinedStartEnd.TimedOut, silent.End);
    }

    [Theory]
    [InlineData(3, 5u, true)]
    [InlineData(3, 0u, false)]
    [InlineData(2, 5u, false)]
    public void Another_connection_with_the_same_account_is_a_take_over(int reason, uint extended, bool takenOver)
    {
        Assert.Equal(takenOver, SessionEnd.IsTakenOver(reason, extended));
    }
}
