using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

public class AckRoundTripsTests
{
    private const long TicksPerSecond = 1000;
    private const long MiB = 1024 * 1024;

    [Fact]
    public void An_acknowledgement_is_timed_from_the_call_that_carried_its_last_byte()
    {
        var trips = new AckRoundTrips();
        trips.OnSent(MiB, 100);
        trips.OnSent(2 * MiB, 150);
        trips.OnSent(3 * MiB, 200);

        Assert.Equal(new AckSample(2 * MiB, 250, MiB), trips.OnAck(2 * MiB, 400));
        Assert.Equal(1, trips.Count);
        Assert.Equal(3 * MiB, trips.SentBytes);
    }

    [Fact]
    public void A_repeated_or_unknown_acknowledgement_gives_no_sample()
    {
        var trips = new AckRoundTrips();
        trips.OnSent(MiB, 0);
        trips.OnSent(2 * MiB, 10);

        Assert.NotNull(trips.OnAck(MiB, 50));
        Assert.Null(trips.OnAck(MiB, 60));
        Assert.Null(trips.OnAck(5 * MiB, 70));
        Assert.Equal(1, trips.Count);
    }

    [Fact]
    public void Positions_that_do_not_grow_are_ignored()
    {
        var trips = new AckRoundTrips();
        trips.OnSent(MiB, 0);
        trips.OnSent(MiB, 50);

        Assert.Equal(new AckSample(MiB, 100, 0), trips.OnAck(MiB, 100));
    }

    [Fact]
    public void The_summary_gives_the_round_trip_and_the_bytes_in_flight()
    {
        var trips = new AckRoundTrips();
        trips.OnSent(2 * MiB, 0);
        trips.OnSent(4 * MiB, 100);
        trips.OnAck(2 * MiB, 300);
        trips.OnSent(6 * MiB, 400);
        trips.OnAck(4 * MiB, 900);

        Assert.Equal(
            "Transfer t1 acknowledgements: 2 matched a call, round trip 550 ms on average, 300 ms at least, 800 ms at most; 2097152 bytes in flight on average when one came, 2097152 at most, 50 % of the window of 4194304 bytes.",
            trips.Describe("t1", TicksPerSecond, 4 * MiB));
    }

    [Fact]
    public void Without_acknowledgements_the_summary_says_so()
    {
        Assert.Equal(
            "Transfer t1 acknowledgements: none matched a call; window 4194304 bytes.",
            new AckRoundTrips().Describe("t1", TicksPerSecond, 4 * MiB));
    }
}
