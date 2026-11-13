using UltimateRDP.Core.Agent;

namespace UltimateRDP.Core.Tests.Direct;

/// <summary>The settings of several direct connections, and how many connections the client opens.</summary>
public class DirectPolicyTests
{
    [Theory]
    [InlineData(8, 8, 1, 1)]
    [InlineData(8, 8, 32L * 1024 * 1024, 1)]
    [InlineData(8, 8, (32L * 1024 * 1024) + 1, 2)]
    [InlineData(8, 8, 6_300_000_000L, 8)]
    [InlineData(4, 8, 6_300_000_000L, 4)]
    [InlineData(8, 2, 6_300_000_000L, 2)]
    [InlineData(16, 16, 6_300_000_000L, 16)]
    [InlineData(8, 8, 0, 1)]
    public void The_client_opens_one_connection_per_started_32_mebibytes_within_the_offer_and_its_setting(int offered, int setting, long bytes, int expected)
    {
        Assert.Equal(expected, DirectTransferPolicy.ConnectionsFor(offered, setting, bytes));
    }

    [Theory]
    [InlineData(null, 8, true)]
    [InlineData(1, 1, true)]
    [InlineData(16, 16, true)]
    [InlineData(0, 8, false)]
    [InlineData(17, 8, false)]
    public void The_connections_setting_is_from_1_to_16(int? value, int expected, bool recognized)
    {
        Assert.Equal(expected, DirectTransferPolicy.ParseConnections(value, out var known));
        Assert.Equal(recognized, known);
    }

    [Theory]
    [InlineData(null, 0, true)]
    [InlineData(0, 0, true)]
    [InlineData(150, 150, true)]
    [InlineData(-1, 0, false)]
    [InlineData(100_001, 0, false)]
    public void The_rate_limit_setting_is_0_or_a_number_of_megabytes_per_second(int? value, int expected, bool recognized)
    {
        Assert.Equal(expected, DirectTransferPolicy.ParseMaxMegabytesPerSecond(value, out var known));
        Assert.Equal(recognized, known);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 85, 85)]
    [InlineData(200, 0, 200)]
    [InlineData(200, 85, 85)]
    [InlineData(50, 85, 85)]
    public void The_limit_of_a_send_is_its_own_setting_and_otherwise_the_limit_of_both_directions(int direct, int send, int expected)
    {
        Assert.Equal(expected, DirectTransferPolicy.SendLimit(direct, send));
    }
}
