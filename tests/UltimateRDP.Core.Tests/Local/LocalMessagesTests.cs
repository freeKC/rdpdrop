using System.Runtime.Versioning;
using System.Text;
using UltimateRDP.Core.Local;
using UltimateRDP.Core.RemoteApp;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Local;

/// <summary>The requests between processes of the same user and session, since 0.9.0.</summary>
public class LocalMessagesTests
{
    [Fact]
    public void A_send_request_reads_back()
    {
        var request = new SendRequest([@"C:\Users\user\Documents\report.pdf", @"\\fileserver\share\Photos"]);

        var decoded = LocalMessages.DecodeSend(LocalMessages.Encode(request));

        Assert.NotNull(decoded);
        Assert.Equal(request.Paths, decoded.Paths);
    }

    [Theory]
    [InlineData("""{"paths":[]}""")]
    [InlineData("""{"paths":["report.pdf"]}""")]
    [InlineData("""{"paths":["\\\\.\\pipe\\x"]}""")]
    [InlineData("""{"paths":["\\\\?\\C:\\x"]}""")]
    [InlineData("""{"paths":["C:\\a\u0007b"]}""")]
    [InlineData("""{"other":1}""")]
    [InlineData("""not json""")]
    public void A_send_request_with_a_relative_device_or_odd_path_or_none_is_refused(string json)
    {
        Assert.Null(LocalMessages.DecodeSend(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void An_open_request_and_an_answer_read_back()
    {
        var open = new OpenRequest(@"C:\rdp\notepad.rdp", LaunchWindow.Hidden, true);
        var answer = new LocalAnswer(false, "Aucun client.");

        Assert.Equal(open, LocalMessages.DecodeOpen(LocalMessages.Encode(open)));
        Assert.Equal(new OpenRequest(null, LaunchWindow.Auto, false), LocalMessages.DecodeOpen(LocalMessages.Encode(new OpenRequest(null, LaunchWindow.Auto, false))));
        Assert.Equal(answer, LocalMessages.DecodeAnswer(LocalMessages.Encode(answer)));
        Assert.Null(LocalMessages.DecodeOpen("""{"path":"x.rdp","window":"auto"}"""u8.ToArray()));
    }

    [Fact]
    public void The_items_of_a_send_are_named_as_a_drop_names_them()
    {
        var plan = LocalMessages.PlanSend([@"C:\data\report.pdf", @"C:\data\Photos", @"D:\"], path => path.EndsWith(".pdf", StringComparison.Ordinal) ? false : true, out var problem, out _);

        Assert.Equal(SendPlanProblem.None, problem);
        Assert.NotNull(plan);
        Assert.Equal([new TransferItem("report.pdf", false), new TransferItem("Photos", true), new TransferItem("Drive D", true)], plan.Value.Items);
        Assert.Equal(@"C:\data\Photos", plan.Value.Drops[1].ClientPath);
    }

    [Theory]
    [InlineData(SendPlanProblem.Missing, @"C:\a\gone.txt")]
    [InlineData(SendPlanProblem.SameName, @"D:\b\report.pdf")]
    public void A_missing_path_or_two_items_of_the_same_name_are_refused(SendPlanProblem expected, string second)
    {
        var plan = LocalMessages.PlanSend([@"C:\a\report.pdf", second], path => path.Contains("gone", StringComparison.Ordinal) ? null : false, out var problem, out var problemPath);

        Assert.Null(plan);
        Assert.Equal(expected, problem);
        Assert.Equal(second, problemPath);
    }
}

/// <summary>The pipe itself, over the local machine, without waiting on real time.</summary>
[SupportedOSPlatform("windows")]
public class SameUserPipeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public void A_request_reaches_the_server_of_the_same_user_and_session_and_its_answer_comes_back()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var purpose = "test" + Guid.NewGuid().ToString("N");
        var logs = new List<string>();
        using var server = SameUserPipe.TryStart(purpose, request => [.. request.Reverse()], line => { lock (logs) { logs.Add(line); } });

        Assert.NotNull(server);
        Assert.Equal([3, 2, 1], SameUserPipe.Call(purpose, [1, 2, 3], Wait));
        Assert.Equal([9], SameUserPipe.Call(purpose, [9], Wait));
        Assert.Contains(purpose, server.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_server_of_the_same_name_is_not_created()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var purpose = "test" + Guid.NewGuid().ToString("N");
        using var first = SameUserPipe.TryStart(purpose, request => request, _ => { });
        var logs = new List<string>();
        using var second = SameUserPipe.TryStart(purpose, request => [], logs.Add);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(logs);
        Assert.Equal([7], SameUserPipe.Call(purpose, [7], Wait));
    }

    [Fact]
    public void Without_a_server_the_call_times_out()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<TimeoutException>(() => SameUserPipe.Call("test" + Guid.NewGuid().ToString("N"), [1], TimeSpan.Zero));
    }

    [Fact]
    public void A_server_that_stopped_frees_its_name()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var purpose = "test" + Guid.NewGuid().ToString("N");
        SameUserPipe.TryStart(purpose, request => request, _ => { })!.Dispose();
        using var again = SameUserPipe.TryStart(purpose, request => [5], _ => { });

        Assert.NotNull(again);
        Assert.Equal([5], SameUserPipe.Call(purpose, [1], Wait));
    }
}
