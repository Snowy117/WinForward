using System.Net;
using WinForward.E2E.Target;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The source census's slot lifetime. A slot belongs to one claim, a claim keeps it while datagrams keep
/// arriving, and <see cref="SourceCensus.Harvest"/> ends the interval: a slot no datagram of the closing
/// interval touched is one a fresh endpoint may claim. What the facts below hold down is that the
/// reclaim does not change what a record means -- <c>sources</c> stays the interval's own increments --
/// while a table that saw more endpoints than it has slots over a process's life still holds the
/// endpoints of the interval it is in.
/// </summary>
public sealed class SourceCensusTests
{
    /// <summary>The endpoints one table holds, as the record contract declares it.</summary>
    private const int DeclaredCapacity = 64;

    private const int FirstWave = 40000;
    private const int SecondWave = 50000;
    private const int ProbeWave = 60000;

    [Fact]
    public void AContinuingEndpointKeepsItsSlotAndIsPublishedAsTheIntervalsOwnDelta()
    {
        var census = new SourceCensus();
        Record(census, FirstWave, DeclaredCapacity);

        var (first, firstOverflow) = Harvest(census);
        Assert.Equal(DeclaredCapacity, first.Count);
        Assert.All(first.Values, datagrams => Assert.Equal(1, datagrams));
        Assert.Equal(0, firstOverflow);

        Record(census, FirstWave, DeclaredCapacity, rounds: 3);

        var (second, secondOverflow) = Harvest(census);
        Assert.Equal(DeclaredCapacity, second.Count);
        Assert.All(second.Values, datagrams => Assert.Equal(3, datagrams));
        Assert.Equal(0, secondOverflow);
    }

    [Fact]
    public void ASlotNoDatagramTouchedForAnIntervalIsReclaimedForAFreshEndpoint()
    {
        var census = new SourceCensus();
        Record(census, FirstWave, DeclaredCapacity);
        var (history, _) = Harvest(census);
        Assert.Equal(DeclaredCapacity, history.Count);

        Record(census, SecondWave, DeclaredCapacity);
        var (second, secondOverflow) = Harvest(census);
        Assert.Equal(DeclaredCapacity, second.Count);
        Assert.All(second.Values, datagrams => Assert.Equal(1, datagrams));
        Assert.Equal(0, secondOverflow);
        Assert.DoesNotContain(second.Keys, history.ContainsKey);

        Record(census, ProbeWave, DeclaredCapacity - 24);
        var (probe, probeOverflow) = Harvest(census);
        Assert.Equal(DeclaredCapacity - 24, probe.Count);
        Assert.Equal(0, probeOverflow);
    }

    [Fact]
    public void AFullTableReportsTheIntervalsUnplacedDatagramsAsOverflow()
    {
        var census = new SourceCensus();
        Record(census, FirstWave, DeclaredCapacity);
        Record(census, SecondWave, ports: 1, rounds: 5);

        var (full, overflow) = Harvest(census);
        Assert.Equal(DeclaredCapacity, full.Count);
        Assert.DoesNotContain(SecondWave, full.Keys);
        Assert.Equal(5, overflow);

        Record(census, FirstWave, DeclaredCapacity);
        Record(census, SecondWave, ports: 1, rounds: 3);

        var (stillFull, stillOverflow) = Harvest(census);
        Assert.Equal(DeclaredCapacity, stillFull.Count);
        Assert.Equal(3, stillOverflow);
        Assert.DoesNotContain(SecondWave, stillFull.Keys);
    }

    [Fact]
    public async Task EveryDatagramIsPublishedExactlyOnceBeneathASummariserRunningWithTheReceiveLoop()
    {
        const int endpoints = 8;
        const int datagrams = 200_000;

        var census = new SourceCensus();
        var recorded = new long[endpoints];
        for (var slot = 0; slot < endpoints; slot++)
        {
            recorded[slot] = 1;
            census.Record(Endpoint(FirstWave + slot));
        }

        var published = new Dictionary<int, long>();
        using var stop = new CancellationTokenSource();
        var token = stop.Token;
        var summariser = Task.Run(() =>
        {
            var interval = new Dictionary<SourceKey, long>();
            while (!token.IsCancellationRequested)
            {
                interval.Clear();
                census.Harvest(interval);
                foreach (var (key, count) in interval)
                {
                    published[key.Port] = published.GetValueOrDefault(key.Port) + count;
                }

                Thread.SpinWait(2_000);
            }
        });

        for (var index = 0; index < datagrams; index++)
        {
            var slot = index % endpoints;
            recorded[slot]++;
            census.Record(Endpoint(FirstWave + slot));
        }

        await stop.CancelAsync();
        await summariser;
        var tail = new Dictionary<SourceKey, long>();
        census.Harvest(tail);
        foreach (var (key, count) in tail)
        {
            published[key.Port] = published.GetValueOrDefault(key.Port) + count;
        }

        Assert.Equal(0, census.HarvestUnplaced());
        Assert.Equal(datagrams + endpoints, published.Values.Sum());
        for (var slot = 0; slot < endpoints; slot++)
        {
            Assert.Equal(recorded[slot], published.GetValueOrDefault(FirstWave + slot));
        }
    }

    private static void Record(SourceCensus census, int firstPort, int ports, int rounds = 1)
    {
        for (var round = 0; round < rounds; round++)
        {
            for (var index = 0; index < ports; index++)
            {
                census.Record(Endpoint(firstPort + index));
            }
        }
    }

    private static IPEndPoint Endpoint(int port) => new(IPAddress.Loopback, port);

    /// <summary>
    /// One interval: the endpoints it published, keyed by port, and the datagrams it could not place.
    /// The facts above use one port per endpoint, so the port is the endpoint's identity here.
    /// </summary>
    private static (Dictionary<int, long> Placed, long Overflow) Harvest(SourceCensus census)
    {
        var totals = new Dictionary<SourceKey, long>();
        census.Harvest(totals);
        var placed = new Dictionary<int, long>();
        foreach (var (key, count) in totals)
        {
            placed[key.Port] = count;
        }

        return (placed, census.HarvestUnplaced());
    }
}
