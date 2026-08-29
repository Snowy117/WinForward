using System.Net;
using WinForward.Core;
using WinForward.Runtime.UdpProxy;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class CoreFlowStructuresTests
{
    [Fact]
    public void SameUdpTupleClaimsOneStateAndDifferentRemoteGetsAnother()
    {
        var local = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var dns1 = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var dns2 = Endpoint.From(IPAddress.Parse("192.0.2.54"), 53);
        var table = new FlowTable();
        var decisionCount = 0;

        var first = table.Claim(FlowKey.Create(local, dns1, TransportProtocol.Udp, FlowOriginKind.Host), () =>
        {
            decisionCount++;
            return new FlowDecision(FlowAction.Proxy, 0, "dns");
        });
        var second = table.Claim(FlowKey.Create(local, dns1, TransportProtocol.Udp, FlowOriginKind.Host), () =>
        {
            decisionCount++;
            return new FlowDecision(FlowAction.Block, 1, null);
        });
        var third = table.Claim(FlowKey.Create(local, dns2, TransportProtocol.Udp, FlowOriginKind.Host), () =>
        {
            decisionCount++;
            return new FlowDecision(FlowAction.Proxy, 0, "dns");
        });

        Assert.Same(first, second);
        Assert.NotSame(first, third);
        Assert.Equal(2, decisionCount);
    }

    [Fact]
    public void PacketLeaseAllowsExactlyOneDisposition()
    {
        using var lease = new PacketLease(new byte[] { 1, 2 });

        Assert.True(lease.TryComplete(PacketDisposition.ProxyConsumed));
        Assert.False(lease.TryComplete(PacketDisposition.Pass));
        Assert.Equal(PacketDisposition.ProxyConsumed, lease.Disposition);
    }

    [Fact]
    public void SetupQueueFailsClosedWhenPacketOrByteLimitIsReached()
    {
        var queue = new BoundedSetupQueue(maxPackets: 2, maxBytes: 4);

        Assert.True(queue.TryEnqueue(new byte[] { 1, 2 }));
        Assert.True(queue.TryEnqueue(new byte[] { 3, 4 }));
        Assert.False(queue.TryEnqueue(new byte[] { 5 }));
        Assert.Equal(2, queue.Count);
        Assert.Equal(4, queue.Bytes);
    }

    [Fact]
    public void FlowTableFailsClosedAtCapacity()
    {
        var table = new FlowTable(capacity: 1);
        var first = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 1), Endpoint.From(IPAddress.Parse("192.0.2.1"), 2), TransportProtocol.Udp, FlowOriginKind.Host);
        var second = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 3), Endpoint.From(IPAddress.Parse("192.0.2.1"), 4), TransportProtocol.Udp, FlowOriginKind.Host);
        Assert.True(table.TryClaim(first, () => FlowDecision.Fallback(FlowAction.Pass), out _));

        Assert.False(table.TryClaim(second, () => FlowDecision.Fallback(FlowAction.Pass), out _));
    }

    [Fact]
    public void FlowTableLookupRefreshesActivityBeforeIdleExpiry()
    {
        var table = new FlowTable();
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var claimed = table.Claim(key, () => FlowDecision.Fallback(FlowAction.Pass));
        var beforeLookup = DateTimeOffset.UtcNow;
        claimed.Touch(beforeLookup - TimeSpan.FromMinutes(2));

        Assert.True(table.TryResolve(key, out var resolved));
        Assert.Same(claimed, resolved);
        Assert.InRange(claimed.LastActivityUtc, beforeLookup, DateTimeOffset.UtcNow);
        Assert.Equal(0, table.RemoveExpired(claimed.LastActivityUtc + TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1), TimeSpan.FromMinutes(1)));
        Assert.Equal(1, table.RemoveExpired(claimed.LastActivityUtc + TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void FlowTableRemoveExpiredHonorsHoldPredicateWithoutTouchingActivity()
    {
        var table = new FlowTable();
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var claimed = table.Claim(key, () => FlowDecision.Fallback(FlowAction.Pass));
        var lastActivity = claimed.LastActivityUtc;
        var now = lastActivity + TimeSpan.FromMinutes(10);

        // A held entry (e.g. a silently relaying TCP redirect session) survives the idle sweep and
        // keeps its activity timestamp untouched.
        Assert.Equal(0, table.RemoveExpired(now, TimeSpan.FromMinutes(1), isHeld: heldKey => heldKey == key));
        Assert.Equal(lastActivity, claimed.LastActivityUtc);

        // Once the hold lapses, the very same sweep time removes the entry at its original idle
        // point — the hold skipped removal without re-arming the idle window.
        Assert.Equal(1, table.RemoveExpired(now, TimeSpan.FromMinutes(1), isHeld: _ => false));
        Assert.False(table.TryResolve(key, out _));
    }

    [Fact]
    public void FlowTableExpiryRemovesCrossAdapterTransportAliases()
    {
        var table = new FlowTable();
        var key = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000),
            Endpoint.From(IPAddress.Parse("198.51.100.53"), 53),
            TransportProtocol.Udp,
            FlowOriginKind.Host,
            new AdapterContext("host", "host", 1));
        var claimed = table.Claim(key, () => FlowDecision.Fallback(FlowAction.Pass));
        claimed.Touch(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2));
        var crossAdapter = key with
        {
            Origin = FlowOriginKind.Forwarded,
            OriginAdapterId = "forwarded",
            OriginAdapterGeneration = 2,
        };

        Assert.Equal(1, table.RemoveExpired(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
        Assert.False(table.TryResolve(crossAdapter, out _));
        Assert.True(table.TryClaimResolved(crossAdapter, () => FlowDecision.Fallback(FlowAction.Block), out var replacement));
        Assert.Equal(FlowAction.Block, replacement!.Decision.Action);
    }

    [Fact]
    public void UdpAssociationUsesOriginalAndRelayAliasesWithoutCrossWiring()
    {
        var original = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var secondOriginal = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.54"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var relay = new RelayAlias(FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 40000), Endpoint.From(IPAddress.Parse("198.51.100.10"), 50000), TransportProtocol.Udp, FlowOriginKind.Host));
        var secondRelay = new RelayAlias(FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 40001), Endpoint.From(IPAddress.Parse("198.51.100.10"), 50001), TransportProtocol.Udp, FlowOriginKind.Host));
        var table = new UdpAssociationTable();
        var now = DateTimeOffset.UtcNow;

        var first = table.Claim(original, relay, now);
        var same = table.Claim(original, relay, now.AddSeconds(1));
        var second = table.Claim(secondOriginal, secondRelay, now.AddSeconds(1));

        Assert.Same(first, same);
        Assert.NotSame(first, second);
        Assert.True(table.TryFindRelay(relay, now.AddSeconds(2), out var found));
        Assert.Same(first, found);
    }

    [Fact]
    public void UdpAssociationExpiryRemovesBothIndexes()
    {
        var original = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var relay = new RelayAlias(original);
        var table = new UdpAssociationTable();
        table.Claim(original, relay, DateTimeOffset.UtcNow);

        Assert.Equal(1, table.RemoveExpired(DateTimeOffset.UtcNow.AddMinutes(2), TimeSpan.FromMinutes(1)));
        Assert.False(table.TryFindOriginal(original, DateTimeOffset.UtcNow, out _));
        Assert.False(table.TryFindRelay(relay, DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void UdpAssociationRejectsRelayAliasCollisionAcrossOriginalFlows()
    {
        var firstOriginal = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var secondOriginal = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53001), Endpoint.From(IPAddress.Parse("192.0.2.54"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var relay = new RelayAlias(FlowKey.Create(Endpoint.From(IPAddress.Loopback, 40000), Endpoint.From(IPAddress.Parse("198.51.100.10"), 50000), TransportProtocol.Udp, FlowOriginKind.Host));
        var table = new UdpAssociationTable();

        Assert.True(table.TryClaim(firstOriginal, relay, DateTimeOffset.UtcNow, out _));
        Assert.False(table.TryClaim(secondOriginal, relay, DateTimeOffset.UtcNow, out var collision));
        Assert.Null(collision);
    }
}
