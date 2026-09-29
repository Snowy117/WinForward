using System.Globalization;
using System.Net;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// The sweep's allocation contract (research F3.3: "no allocation under any table lock"). The
/// <see cref="FlowTable"/> sweep is the one site that already satisfies it — it collects expired keys
/// into a reused scratch list — so it is gated here exactly. The other sites the research named
/// (<c>TcpRedirectTable</c>, <c>TcpRedirectSessionStore</c>, <c>UdpProxyCoordinator</c>,
/// <c>UdpAssociationPool</c>) build their retirement sets with LINQ <c>Where/Select/Distinct/ToArray</c>
/// and therefore allocate today; their byte baselines are recorded as the F3 target in
/// <c>benchmarks/results/2026-09-29-benchmark-coverage/</c> and their gates land with the fix, because a
/// gate added now would be red on an unmodified tree.
/// </summary>
public sealed class SweepAllocationGateTests
{
    private const int Flows = 4_096;

    private static readonly IPAddress s_client = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destination = IPAddress.Parse("192.0.2.53");

    [Fact]
    public void FlowTableSweepAllocatesNoManagedBytes()
    {
        var table = new FlowTable(capacity: Flows + 16);
        var keys = BuildKeys();

        // Two warm sweeps: the first grows the scratch list and returns every state to the pool, so the
        // measured sweep sees steady state rather than one-time growth.
        Seed(table, keys);
        Assert.Equal(Flows, table.RemoveExpired(DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.Zero));
        Seed(table, keys);
        Assert.Equal(Flows, table.RemoveExpired(DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.Zero));
        Seed(table, keys);

        var removed = 0;
        var allocated = MeasureAllocated(() => removed = table.RemoveExpired(DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.Zero));

        Assert.Equal(Flows, removed);
        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// The gate's own discriminating power: the same measurement path must see a known allocation, or
    /// "0 bytes" would only mean the probe cannot see anything.
    /// </summary>
    [Fact]
    public void AllocationProbeSeesAKnownAllocation()
    {
        var allocated = MeasureAllocated(static () =>
        {
            var probe = new byte[4_096];
            GC.KeepAlive(probe);
        });

        Assert.True(allocated >= 4_096, string.Create(CultureInfo.InvariantCulture, $"The allocation probe reported {allocated} bytes for a 4,096-byte allocation."));
    }

    private static long MeasureAllocated(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void Seed(FlowTable table, FlowKey[] keys)
    {
        foreach (var key in keys)
        {
            if (!table.TryClaimResolved(key, static () => FlowDecision.Fallback(FlowAction.Pass), out _))
            {
                Assert.Fail("The sweep gate could not seed the flow table.");
            }
        }
    }

    private static FlowKey[] BuildKeys()
    {
        var keys = new FlowKey[Flows];
        for (var index = 0; index < keys.Length; index++)
        {
            keys[index] = FlowKey.Create(
                Endpoint.From(s_client, checked((ushort)(10_000 + index))),
                Endpoint.From(s_destination, 443),
                TransportProtocol.Tcp,
                FlowOriginKind.Host);
        }

        return keys;
    }
}
