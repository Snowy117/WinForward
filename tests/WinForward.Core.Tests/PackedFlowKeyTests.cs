using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.Core.Tests.FlowBuilders;

namespace WinForward.Core.Tests;

/// <summary>
/// The packed-key contract and the F2 re-proof it owes: the key compares and hashes with integer
/// operations only, the packing round-trips every endpoint fact (including the IPv6 scope id), the
/// packed hash and canonical slot are the same values as their materialized twins over the corpus
/// that can tell them apart, and the two warm-cache validations that consume them still corroborate
/// the full key and the transport tuple.
/// </summary>
public sealed class PackedFlowKeyTests
{
    private static readonly Endpoint s_clientV4 = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53_000);
    private static readonly Endpoint s_serverV4 = Endpoint.From(IPAddress.Parse("192.0.2.53"), 443);
    private static readonly Endpoint s_clientV6 = Endpoint.From(IPAddress.Parse("2001:db8::10"), 53_000);
    private static readonly Endpoint s_serverV6 = Endpoint.From(IPAddress.Parse("2001:db8::53"), 443);

    [Fact]
    public void FlowKeyFitsOneCacheLineAndHasNoReferenceTypedFields()
    {
        var size = Unsafe.SizeOf<FlowKey>();
        Assert.True(size <= 64, string.Create(CultureInfo.InvariantCulture, $"FlowKey is {size} bytes"));
        Assert.DoesNotContain(
            typeof(FlowKey).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            field => !field.FieldType.IsValueType);
    }

    [Fact]
    public void FlowKeyPackedRoundTripsEndpoints()
    {
        foreach (var (key, local, remote) in EndpointCorpus())
        {
            Assert.Equal(local, key.Local);
            Assert.Equal(remote, key.Remote);
            Assert.Equal(local.Address.Family, key.Local.Address.Family);
            Assert.Equal(local.Address.ScopeId, key.Local.Address.ScopeId);
            Assert.Equal(local.Port, key.Local.Port);
            Assert.Equal(remote.Address.ScopeId, key.Remote.Address.ScopeId);
        }
    }

    [Fact]
    public void ReverseSwapsEndpointsAndScopes()
    {
        foreach (var (key, local, remote) in EndpointCorpus())
        {
            var reverse = key.Reverse();
            Assert.Equal(remote, reverse.Local);
            Assert.Equal(local, reverse.Remote);

            // The canonical slot is orientation-independent: both orientations select one slot.
            Assert.Equal(Canonical(key), Canonical(reverse));
        }
    }

    [Fact]
    public void PackedAndMaterializedHashesAgree()
    {
        foreach (var (key, _, _) in HashCorpus())
        {
            var materialized = FlowHash.Combine(key.AddressFamily, key.Protocol, key.Local, key.Remote);
            Assert.Equal(materialized, Packed(key));
            Assert.Equal(materialized, key.GetHashCode());
        }
    }

    [Fact]
    public void PackedAndMaterializedCanonicalHashesAgree()
    {
        foreach (var (key, _, _) in HashCorpus())
        {
            var materialized = IndependentCanonical(key);
            Assert.Equal(materialized, Canonical(key));
            Assert.Equal(materialized, FlowHash.CombineCanonical(key.AddressFamily, key.Protocol, key.Local, key.Remote));
            Assert.Equal(materialized, FlowHash.CombineCanonical(key.AddressFamily, key.Protocol, key.Remote, key.Local));
        }
    }

    [Fact]
    public void CanonicalSlotIsOrderIndependent()
    {
        foreach (var (key, _, _) in HashCorpus())
        {
            Assert.Equal(Canonical(key), Canonical(key.Reverse()));
        }
    }

    [Fact]
    public void IsReverseOfMatchesTheEndpointComparison()
    {
        foreach (var (key, _, remote) in EndpointCorpus())
        {
            var reverse = key.Reverse();
            Assert.True(key.Local == reverse.Remote && key.Remote == reverse.Local, "Reverse must swap the endpoints");
            Assert.True(key.IsReverseOf(reverse));
            Assert.True(reverse.IsReverseOf(key));
            Assert.Equal(key.Local == key.Remote, key.IsReverseOf(key));

            var nearRemote = Endpoint.From(remote.Address, (ushort)(remote.Port + 1));
            var unrelated = FlowKey.Create(remote, nearRemote, key.Protocol, key.Origin, key.OriginAdapterSlot, key.OriginAdapterGeneration);
            Assert.False(key.IsReverseOf(unrelated));
        }
    }

    [Fact]
    public void OriginalKeyCacheStillValidatesTheWholeKey()
    {
        var table = new TcpRedirectTable();
        var slotA = SlotOf("cache-adapter-a", 1);
        var slotB = SlotOf("cache-adapter-b", 1);
        var keyA = FlowKey.Create(s_clientV4, s_serverV4, TransportProtocol.Tcp, FlowOriginKind.Host, slotA, 1);
        var keyB = FlowKey.Create(s_clientV4, s_serverV4, TransportProtocol.Tcp, FlowOriginKind.Host, slotB, 1);

        Assert.True(table.TryClaim(keyA, keyA.Remote, 0x1234, Endpoint.From(IPAddress.Loopback, 41_000), forwardLocalAddress: null, DateTimeOffset.UtcNow, out var association));
        Assert.NotNull(association);
        // Warm the original-key cache with A.
        Assert.True(table.TryResolveByOriginal(keyA, DateTimeOffset.UtcNow, out var cached));
        Assert.Same(association, cached);

        // The cache slot is keyed on the transport-only hash, so B lands in the same slot; the full-key
        // compare must reject it and fall through to the gated authority, which has no B.
        Assert.False(table.TryResolveByOriginal(keyB, DateTimeOffset.UtcNow, out var other));
        Assert.Null(other);
    }

    private static IEnumerable<(FlowKey Key, Endpoint Local, Endpoint Remote)> EndpointCorpus()
    {
        yield return (FlowKey.Create(s_clientV4, s_serverV4, TransportProtocol.Tcp, FlowOriginKind.Host), s_clientV4, s_serverV4);
        yield return (FlowKey.Create(s_clientV6, s_serverV6, TransportProtocol.Udp, FlowOriginKind.Forwarded, SlotOf("v6-adapter", 7), 7), s_clientV6, s_serverV6);

        var scopedLocal = Endpoint.From(new IPAddressValue(Bits("fe80::10"), AddressFamilyKind.IPv6, 9), 53_000);
        var scopedRemote = Endpoint.From(new IPAddressValue(Bits("fe80::53"), AddressFamilyKind.IPv6, 4), 443);
        yield return (FlowKey.Create(scopedLocal, scopedRemote, TransportProtocol.Tcp, FlowOriginKind.Host), scopedLocal, scopedRemote);

        yield return (FlowKey.Create(s_clientV4, s_clientV4, TransportProtocol.Udp, FlowOriginKind.Host), s_clientV4, s_clientV4);
    }

    private static IEnumerable<(FlowKey Key, Endpoint Local, Endpoint Remote)> HashCorpus()
    {
        foreach (var entry in EndpointCorpus()) yield return entry;

        // A pair whose high address halves order the opposite way from their low halves: with
        // low-half-first canonical ordering the packed and materialized slot functions still agree on
        // a symmetric corpus, but the slot stops matching CombineCanonical.
        var lowOrdered = Endpoint.From(new IPAddressValue(new UInt128(upper: 1, lower: ulong.MaxValue), AddressFamilyKind.IPv6), 80);
        var highOrdered = Endpoint.From(new IPAddressValue(new UInt128(upper: 2, lower: 0), AddressFamilyKind.IPv6), 80);
        yield return (FlowKey.Create(lowOrdered, highOrdered, TransportProtocol.Tcp, FlowOriginKind.Host), lowOrdered, highOrdered);

        var random = new Random(20260930);
        for (var index = 0; index < 2_000; index++)
        {
            var ipv6 = index % 2 == 0;
            var local = Endpoint.From(RandomAddress(random, ipv6), (ushort)random.Next(1, 65_535));
            var remote = Endpoint.From(RandomAddress(random, ipv6), (ushort)random.Next(1, 65_535));
            yield return (FlowKey.Create(local, remote, index % 3 == 0 ? TransportProtocol.Udp : TransportProtocol.Tcp, FlowOriginKind.Host), local, remote);
        }
    }

    private static IPAddressValue RandomAddress(Random random, bool ipv6)
    {
        Span<byte> bytes = stackalloc byte[16];
        random.NextBytes(bytes);
        return ipv6 ? IPAddressValue.FromIPv6(bytes, (uint)random.Next(0, 4)) : IPAddressValue.FromIPv4(bytes[..4]);
    }

    private static UInt128 Bits(string address) => IPAddressValue.From(IPAddress.Parse(address)).Bits;

    private static int Packed(FlowKey key) => FlowHash.CombinePacked(
        (byte)key.AddressFamily, (byte)key.Protocol,
        key.LocalLow, key.LocalHigh, key.LocalPort,
        key.RemoteLow, key.RemoteHigh, key.RemotePort);

    private static int Canonical(FlowKey key) => FlowHash.CombineCanonicalPacked(
        (byte)key.AddressFamily, (byte)key.Protocol,
        key.LocalLow, key.LocalHigh, key.LocalPort,
        key.RemoteLow, key.RemoteHigh, key.RemotePort);

    /// <summary>
    /// The independent materialized canonical form: the pre-F4 total order over materialized
    /// endpoints (address bits via <c>UInt128.CompareTo</c>, then port — equal endpoints order either
    /// way) hashed through the key's materialized side. The production canonical entry point now
    /// delegates to the packed expression, so without this local oracle the corpus's divergent
    /// low/high-half pair could not tell a self-consistent packed ordering change from the
    /// documented order.
    /// </summary>
    private static int IndependentCanonical(FlowKey key)
    {
        var local = key.Local;
        var remote = key.Remote;
        var bits = local.Address.Bits.CompareTo(remote.Address.Bits);
        if (bits != 0 ? bits > 0 : local.Port > remote.Port) (local, remote) = (remote, local);
        return FlowHash.Combine(key.AddressFamily, key.Protocol, local, remote);
    }
}
