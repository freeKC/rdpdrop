using UltimateRDP.Core.Agent;

namespace UltimateRDP.Core.Tests.Agent;

public class ChannelRetryPolicyTests
{
    private static readonly TimeSpan LongAgo = TimeSpan.FromMinutes(10);

    private static ChannelAttempt Attempt(
        bool remote = true,
        bool disconnected = false,
        int error = 0,
        int failures = 1,
        TimeSpan? sinceConnect = null) =>
        new(remote, disconnected, error, failures, sinceConnect ?? LongAgo);

    [Fact]
    public void Delays_are_two_and_ten_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), ChannelRetryPolicy.ShortDelay);
        Assert.Equal(TimeSpan.FromSeconds(10), ChannelRetryPolicy.LongDelay);
    }

    [Fact]
    public void A_session_that_is_not_remote_waits_long()
    {
        Assert.Equal(ChannelRetryPolicy.LongDelay, ChannelRetryPolicy.NextDelay(Attempt(remote: false)));
    }

    [Fact]
    public void A_session_that_is_not_remote_waits_long_even_just_after_the_start()
    {
        Assert.Equal(ChannelRetryPolicy.LongDelay, ChannelRetryPolicy.NextDelay(Attempt(remote: false, sinceConnect: TimeSpan.Zero)));
    }

    [Fact]
    public void A_disconnected_session_waits_long()
    {
        Assert.Equal(ChannelRetryPolicy.LongDelay, ChannelRetryPolicy.NextDelay(Attempt(disconnected: true, sinceConnect: TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(ChannelRetryPolicy.ErrorFileNotFound)]
    [InlineData(ChannelRetryPolicy.ErrorNotFound)]
    public void A_channel_that_does_not_exist_waits_long(int error)
    {
        Assert.Equal(ChannelRetryPolicy.LongDelay, ChannelRetryPolicy.NextDelay(Attempt(error: error)));
    }

    [Fact]
    public void A_channel_that_does_not_exist_waits_short_just_after_a_connection()
    {
        var attempt = Attempt(error: ChannelRetryPolicy.ErrorFileNotFound, sinceConnect: TimeSpan.FromSeconds(5));

        Assert.Equal(ChannelRetryPolicy.ShortDelay, ChannelRetryPolicy.NextDelay(attempt));
    }

    [Fact]
    public void The_grace_period_ends_after_thirty_seconds()
    {
        var attempt = Attempt(error: ChannelRetryPolicy.ErrorFileNotFound, sinceConnect: ChannelRetryPolicy.ConnectGracePeriod);

        Assert.Equal(ChannelRetryPolicy.LongDelay, ChannelRetryPolicy.NextDelay(attempt));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 10)]
    [InlineData(50, 10)]
    public void An_unexpected_failure_waits_short_then_long_when_it_repeats(int failures, int expectedSeconds)
    {
        var attempt = Attempt(error: 5, failures: failures);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ChannelRetryPolicy.NextDelay(attempt));
    }

    [Fact]
    public void A_closed_channel_is_reopened_after_the_short_delay()
    {
        Assert.Equal(ChannelRetryPolicy.ShortDelay, ChannelRetryPolicy.NextDelay(Attempt(error: 0, failures: 0)));
    }

    [Fact]
    public void No_attempt_ever_waits_less_than_two_seconds()
    {
        foreach (var remote in new[] { true, false })
        {
            foreach (var disconnected in new[] { true, false })
            {
                foreach (var error in new[] { 0, 2, 5, 87, 1168 })
                {
                    foreach (var failures in new[] { 0, 1, 3, 100 })
                    {
                        foreach (var since in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(29), LongAgo })
                        {
                            var delay = ChannelRetryPolicy.NextDelay(new ChannelAttempt(remote, disconnected, error, failures, since));
                            Assert.True(delay >= ChannelRetryPolicy.ShortDelay, $"{delay} for {remote} {disconnected} {error} {failures} {since}");
                        }
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(false, false, 5, true)]
    [InlineData(true, true, 5, true)]
    [InlineData(true, false, 2, true)]
    [InlineData(true, false, 1168, true)]
    [InlineData(true, false, 5, false)]
    [InlineData(true, false, 87, false)]
    public void Failures_of_a_session_without_client_are_expected(bool remote, bool disconnected, int error, bool expected)
    {
        Assert.Equal(expected, ChannelRetryPolicy.IsExpected(Attempt(remote, disconnected, error)));
    }
}
