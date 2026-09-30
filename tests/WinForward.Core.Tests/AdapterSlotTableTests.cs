using System.Globalization;
using System.Net;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// The interned-adapter contract: a stable ID keeps one slot for the process lifetime (so a key
/// minted before an adapter-list refresh still resolves), slots are monotone and never reused (so an
/// old key can never compare equal to a different adapter's), the table refuses identity it cannot
/// mint, and a key compares its slot <em>and</em> the generation the field exists for.
/// </summary>
public sealed class AdapterSlotTableTests
{
    private static readonly Endpoint s_client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53_000);
    private static readonly Endpoint s_server = Endpoint.From(IPAddress.Parse("192.0.2.53"), 443);

    [Fact]
    public void AdapterSlotTableRoundTripsStableId()
    {
        var table = new AdapterSlotTable();

        Assert.True(table.TryIntern("adapter-a", 7, "Ethernet A", out var first));
        Assert.True(table.TryIntern("adapter-b", 8, "Ethernet B", out var second));
        Assert.NotEqual(first, second);
        Assert.NotEqual(AdapterSlotTable.NoSlot, first);

        Assert.True(table.TryResolve(first, out var metadata));
        Assert.Equal("adapter-a", metadata!.StableId);
        Assert.Equal("Ethernet A", metadata.FriendlyName);
        Assert.Equal(7, metadata.Generation);
        Assert.Equal(first, metadata.Slot);
        Assert.Equal(2, table.CountForDiagnostics);
        Assert.False(table.ExhaustedForDiagnostics);
    }

    [Fact]
    public void TryInternIsIdempotentPerStableIdAndSlotsAreNeverReused()
    {
        var table = new AdapterSlotTable();
        Assert.True(table.TryIntern("adapter-a", 1, "A", out var first));
        Assert.True(table.TryIntern("adapter-b", 1, "B", out var second));

        // Re-interning is a refresh: same slot, republished metadata, no new identity.
        Assert.True(table.TryIntern("adapter-a", 2, "A renamed", out var again));
        Assert.Equal(first, again);
        Assert.Equal(2, table.CountForDiagnostics);
        Assert.True(table.TryResolve(first, out var refreshed));
        Assert.Equal(2, refreshed!.Generation);
        Assert.Equal("A renamed", refreshed.FriendlyName);

        Assert.NotEqual(first, second);
        Assert.True(second > first, "slots are allocated monotonically");
    }

    [Fact]
    public void AdapterSlotSurvivesARefresh()
    {
        var table = new AdapterSlotTable();
        Assert.True(table.TryIntern("adapter-a", 1, "A", out var slot));
        var before = TableKey(slot, 1);

        // A new enumeration bumps the generation for every adapter; the slot must not move, or every
        // key minted before the refresh would stop resolving through its adapter identity.
        Assert.True(table.TryIntern("adapter-a", 2, "A", out var afterRefresh));
        Assert.Equal(slot, afterRefresh);

        var after = TableKey(afterRefresh, 2);
        Assert.NotEqual(before, after);
        Assert.Equal(before.OriginAdapterSlot, after.OriginAdapterSlot);
    }

    [Fact]
    public void AdapterSlotTableRefusesAnUnregisteredAdapter()
    {
        var table = new AdapterSlotTable();
        Assert.True(table.TryIntern("adapter-a", 1, "A", out var slot));

        Assert.False(table.TryResolve(AdapterSlotTable.NoSlot, out var none));
        Assert.Null(none);
        Assert.False(table.TryResolve((ushort)(slot + 100), out _));
        Assert.False(table.TryGetSlot("never-interned", out _));

        // The cold table overload yields the adapter-less slot rather than minting identity.
        var key = FlowKey.Create(s_client, s_server, TransportProtocol.Tcp, FlowOriginKind.Forwarded, table, "never-interned", 3);
        Assert.Equal(AdapterSlotTable.NoSlot, key.OriginAdapterSlot);
    }

    [Fact]
    public void AnAdapterThatCannotBeInternedIsRefusedNotAliased()
    {
        var table = new AdapterSlotTable();
        for (var index = 0; index < ushort.MaxValue; index++)
        {
            Assert.True(table.TryIntern(string.Create(CultureInfo.InvariantCulture, $"adapter-{index}"), 1, friendlyName: null, out _));
        }

        Assert.True(table.ExhaustedForDiagnostics);
        Assert.Equal(ushort.MaxValue, table.CountForDiagnostics);

        // Exhaustion refuses: no slot is reused and no adapter is collapsed onto NoSlot.
        Assert.False(table.TryIntern("adapter-overflow", 1, friendlyName: null, out var refused));
        Assert.Equal(AdapterSlotTable.NoSlot, refused);
        Assert.Equal(ushort.MaxValue, table.CountForDiagnostics);

        // An interned adapter still resolves after the refusal.
        Assert.True(table.TryGetSlot("adapter-1", out var existing));
        Assert.True(table.TryResolve(existing, out var metadata));
        Assert.Equal("adapter-1", metadata!.StableId);
    }

    [Fact]
    public void FlowKeyEqualityKeepsTheGeneration()
    {
        var table = new AdapterSlotTable();
        Assert.True(table.TryIntern("adapter-a", 1, "A", out var slot));
        var first = TableKey(slot, 1);
        var sameEnumeration = TableKey(slot, 1);
        var nextEnumeration = TableKey(slot, 2);
        var otherAdapter = TableKey((ushort)(slot + 1), 1);

        Assert.Equal(first, sameEnumeration);
        Assert.Equal(first.GetHashCode(), sameEnumeration.GetHashCode());
        Assert.NotEqual(first, nextEnumeration);
        Assert.NotEqual(first, otherAdapter);
    }

    [Fact]
    public void TwoAdapterSlotsOnTheSameTupleStillAliasThroughTheTransportIndex()
    {
        var table = new AdapterSlotTable();
        Assert.True(table.TryIntern("adapter-a", 1, "A", out var slotA));
        Assert.True(table.TryIntern("adapter-b", 1, "B", out var slotB));

        var flowTable = new FlowTable();
        var onAdapterA = TableKey(slotA, 1);
        var onAdapterB = TableKey(slotB, 1);

        Assert.True(flowTable.TryClaimResolved(onAdapterA, static () => FlowDecision.Fallback(FlowAction.Pass), out var claimed));
        Assert.Equal(onAdapterA, claimed!.Key);

        // Origin-agnostic resolution: a key for the same transport tuple on another adapter is the
        // same logical flow, exactly as an origin-flipped key is today.
        Assert.True(flowTable.TryResolve(onAdapterB, out var resolved));
        Assert.Same(claimed, resolved);
    }

    private static FlowKey TableKey(ushort slot, long generation) =>
        FlowKey.Create(s_client, s_server, TransportProtocol.Tcp, FlowOriginKind.Forwarded, slot, generation);
}
