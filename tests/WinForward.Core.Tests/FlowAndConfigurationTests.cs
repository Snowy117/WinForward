using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class FlowAndConfigurationTests
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
    public void EndpointEqualityUsesAddressValueRatherThanIpAddressObjectIdentity()
    {
        var first = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var second = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void EndpointFactoryRejectsNullAddressWithArgumentException()
    {
        Assert.Throws<ArgumentNullException>(() => Endpoint.From(null!, 53));
    }

    [Fact]
    public void PolicyUsesFirstMatchingRule()
    {
        var context = new FlowContext(
            FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host),
            "dns.exe", null, null, null, 53);
        var policy = new PolicySnapshot(
        [
            new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns.exe" }), new FlowDecision(FlowAction.Block, 0, null)),
            new(new RuleMatcher(RemotePorts: [(53, 53)]), new FlowDecision(FlowAction.Proxy, 1, "dns"))
        ], FlowAction.Pass);

        var decision = policy.Evaluate(context);

        Assert.Equal(FlowAction.Block, decision.Action);
        Assert.Equal(0, decision.RuleIndex);
    }

    [Fact]
    public void ForwardedPolicySkipsUnqualifiedRulesAndConfiguredFallback()
    {
        var key = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), 443),
            TransportProtocol.Tcp,
            FlowOriginKind.Forwarded,
            new AdapterContext("id-b", "vEthernet B", 1));
        var context = new FlowContext(key, null, null, "id-b", "vEthernet B", 443);
        var policy = new PolicySnapshot(
        [
            new(new RuleMatcher(), new FlowDecision(FlowAction.Proxy, 0, "primary")),
            new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }), new FlowDecision(FlowAction.Proxy, 1, "primary"))
        ], FlowAction.Block);

        var decision = policy.EvaluateForwarded(context);

        Assert.Equal(FlowAction.Pass, decision.Action);
        Assert.Null(decision.RuleIndex);
        Assert.Equal(FlowAction.Proxy, policy.Evaluate(context).Action);
    }

    [Fact]
    public void ForwardedPolicyPreservesQualifiedRuleOrderAndMatcherConditions()
    {
        Assert.True(IpPrefix.TryParse("192.0.2.0/24", out var network));
        var key = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), 443),
            TransportProtocol.Udp,
            FlowOriginKind.Forwarded,
            new AdapterContext("id-a", "vEthernet A", 1));
        var context = new FlowContext(key, null, null, "id-a", "vEthernet A", 443);
        var policy = new PolicySnapshot(
        [
            new(new RuleMatcher(), new FlowDecision(FlowAction.Block, 0, null)),
            new(new RuleMatcher(
                AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" },
                Protocols: new HashSet<TransportProtocol> { TransportProtocol.Tcp }),
                new FlowDecision(FlowAction.Block, 1, null)),
            new(new RuleMatcher(
                AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vEthernet A" },
                Protocols: new HashSet<TransportProtocol> { TransportProtocol.Udp },
                AddressFamilies: new HashSet<AddressFamilyKind> { AddressFamilyKind.IPv4 },
                RemoteNetworks: [network],
                RemotePorts: [(443, 443)]),
                new FlowDecision(FlowAction.Proxy, 2, "primary")),
            new(new RuleMatcher(AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" }), new FlowDecision(FlowAction.Pass, 3, null))
        ], FlowAction.Block);

        var decision = policy.EvaluateForwarded(context);

        Assert.Equal(FlowAction.Proxy, decision.Action);
        Assert.Equal(2, decision.RuleIndex);
    }

    [Fact]
    public void ProcessSelectorMatchesFilenameOrNormalizedFullPathExactly()
    {
        var filenameContext = new FlowContext(
            FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host),
            null, @"C:\Windows\System32\DNS.EXE", null, null, 53);
        var filenamePolicy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns.exe" }), new FlowDecision(FlowAction.Block, 0, null))],
            FlowAction.Pass);

        Assert.Equal(FlowAction.Block, filenamePolicy.Evaluate(filenameContext).Action);

        var pathContext = filenameContext with { ProcessName = "dns.exe", ProcessPath = @"C:\Program Files\WinForward\dns.exe" };
        var pathPolicy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "c:/program files/winforward/dns.exe" }), new FlowDecision(FlowAction.Block, 0, null))],
            FlowAction.Pass);

        Assert.Equal(FlowAction.Block, pathPolicy.Evaluate(pathContext).Action);
        Assert.Equal(FlowAction.Pass, new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns" }), new FlowDecision(FlowAction.Block, 0, null))],
            FlowAction.Pass).Evaluate(pathContext).Action);
    }

    [Fact]
    public void SocksUdpRoundTripPreservesDnsPayloadAndEndpoint()
    {
        var payload = new byte[] { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01 };
        var address = IPAddress.Parse("2001:db8::53");
        var encoded = Socks5UdpCodec.Encode(address, 53, payload);

        Assert.True(Socks5UdpCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(address, decoded.DestinationAddress);
        Assert.Equal((ushort)53, decoded.DestinationPort);
        Assert.Equal(payload, decoded.Payload.ToArray());
    }

    [Fact]
    public void SocksUdpRejectsFragmentedFrames()
    {
        var encoded = Socks5UdpCodec.Encode(IPAddress.Parse("192.0.2.53"), 53, [1, 2, 3]);
        encoded[2] = 1;

        Assert.False(Socks5UdpCodec.TryDecode(encoded, out _));
    }

    [Fact]
    public void SocksUdpDomainRoundTripPreservesPayload()
    {
        var encoded = Socks5UdpCodec.Encode("dns.example", 53, [0xab, 0xcd]);

        Assert.True(Socks5UdpCodec.TryDecode(encoded, out var decoded));
        Assert.Null(decoded.DestinationAddress);
        Assert.Equal("dns.example", decoded.DestinationDomain);
        Assert.Equal(new byte[] { 0xab, 0xcd }, decoded.Payload.ToArray());
    }

    [Fact]
    public void PolicyMatchesRemoteCidr()
    {
        Assert.True(IpPrefix.TryParse("192.0.2.0/24", out var network));
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 50000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);
        var context = new FlowContext(key, null, null, null, null, 53);
        var policy = new PolicySnapshot([new(new RuleMatcher(RemoteNetworks: [network]), new FlowDecision(FlowAction.Block, 0, null))], FlowAction.Pass);

        Assert.Equal(FlowAction.Block, policy.Evaluate(context).Action);
    }

    [Fact]
    public void IPv6PrefixesNormalizeAndMatchOnlyTheirPrefix()
    {
        Assert.True(IpPrefix.TryParse("2001:db8:1::1234/64", out var prefix));

        Assert.Equal(IPAddress.Parse("2001:db8:1::"), prefix.Network);
        Assert.True(prefix.Contains(IPAddress.Parse("2001:db8:1::ffff")));
        Assert.False(prefix.Contains(IPAddress.Parse("2001:db8:2::1")));
        Assert.False(prefix.Contains(IPAddress.Parse("192.0.2.1")));
    }

    [Fact]
    public void IpPrefixRejectsNullInputsWithoutThrowing()
    {
        Assert.False(IpPrefix.TryParse(null, out var prefix));
        Assert.False(prefix.Contains(null));
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

    [Fact]
    public void Socks5UsernamePasswordAndGreetingUseRfcLengths()
    {
        Assert.Equal(new byte[] { 5, 1, 0 }, Socks5Messages.Greeting(credentials: false));
        Assert.Equal(new byte[] { 5, 2, 0, 2 }, Socks5Messages.Greeting(credentials: true));
        Assert.Equal(new byte[] { 1, 1, (byte)'u', 1, (byte)'p' }, Socks5Messages.UsernamePassword("u", "p"));
    }

    [Fact]
    public void Socks5RequestsCarryCommandAddressAndPort()
    {
        var request = Socks5Messages.Request(Socks5Command.UdpAssociate, IPAddress.Parse("192.0.2.53"), 5353);

        Assert.Equal(new byte[] { 5, 3, 0, 1, 192, 0, 2, 53, 0x14, 0xe9 }, request);
    }

    [Fact]
    public void Socks5ReplyParserReturnsStatusAndBoundPort()
    {
        var reply = new byte[] { 5, 0, 0, 1, 192, 0, 2, 53, 0x14, 0xe9 };

        Assert.Equal(Socks5ReplyKind.Success, Socks5Messages.TryParseReply(reply, out var status, out var addressType, out var port));
        Assert.Equal((byte)0, status);
        Assert.Equal((byte)1, addressType);
        Assert.Equal((ushort)5353, port);
    }

    [Fact]
    public void Socks5TruncatedFailureReplyIsRejectedNotParsedAsSuccess()
    {
        // L2: a failure reply (REP 1-8) must be reported as a definite Failure without a bound
        // address; it must never "parse" into a success. An undersized failure reply is still a
        // definite failure (the status is the diagnostic).
        var undersizedFailure = new byte[] { 5, 5 };
        Assert.Equal(Socks5ReplyKind.Failure, Socks5Messages.TryParseReply(undersizedFailure, out var status, out _, out _));
        Assert.Equal((byte)5, status);

        // A truncated success reply (< 8 bytes) is invalid, never silently parsed as a bounded
        // success — the caller must distinguish this from a well-formed reply.
        var truncatedSuccess = new byte[] { 5, 0, 0, 1, 192, 0, 2 };
        Assert.Equal(Socks5ReplyKind.Invalid, Socks5Messages.TryParseReply(truncatedSuccess, out _, out _, out _));
    }

    [Fact]
    public void UdpAssociateWildcardReplySubstitutesControlPeerButKeepsPort()
    {
        // M1: a UDP ASSOCIATE reply returning 0.0.0.0/:: is those-substituted with the TCP control
        // peer (RFC-endorsed fallback), but the server-provided BND port is preserved.
        var controlPeer = IPAddress.Parse("192.0.2.100");
        var mappedAny = IPAddress.Parse("::ffff:0.0.0.0");
        var normalized = Socks5Messages.NormalizeBndAddress(mappedAny, controlPeer, Socks5Command.UdpAssociate);
        Assert.Equal(controlPeer, normalized);
        // The port is not part of NormalizeBndAddress; the caller keeps reply[portOffset..]. The
        // IPv6Any wildcard is substituted too.
        Assert.Equal(controlPeer, Socks5Messages.NormalizeBndAddress(IPAddress.IPv6Any, controlPeer, Socks5Command.UdpAssociate));
    }

    [Fact]
    public void ConnectReplyIsNeverSubstituted()
    {
        // M1: a CONNECT reply's BND.ADDR is not used for routing, so an unspecified/mapped reply
        // must NOT be substituted with the control peer.
        var mappedAny = IPAddress.Parse("::ffff:0.0.0.0");
        var normalized = Socks5Messages.NormalizeBndAddress(mappedAny, IPAddress.Parse("192.0.2.100"), Socks5Command.Connect);
        Assert.Equal(IPAddress.Any, normalized);
    }

    [Fact]
    public void NormalizeBndAddressMapsIpv4MappedToPlainIpv4()
    {
        // M1: an IPv4-mapped ::ffff:a.b.c.d reply is normalized to its IPv4 form.
        var mapped = IPAddress.Parse("::ffff:192.0.2.53");
        var controlPeer = IPAddress.Parse("192.0.2.100");
        var normalized = Socks5Messages.NormalizeBndAddress(mapped, controlPeer, Socks5Command.UdpAssociate);
        Assert.Equal(IPAddress.Parse("192.0.2.53"), normalized);
    }

    [Fact]
    public void NormalizeBndAddressCarriesControlPeerScopeOverIpv6Reply()
    {
        // M2: a genuine IPv6 relay address reconstructed from raw bytes has ScopeId 0; it inherits
        // the control peer's non-zero interface scope so a link-local relay routes correctly.
        var linkLocal = new IPAddress(IPAddress.Parse("fe80::1").GetAddressBytes(), 0);
        var scopeSource = new IPAddress(IPAddress.Parse("fe80::10").GetAddressBytes(), 7);
        var normalized = Socks5Messages.NormalizeBndAddress(linkLocal, scopeSource, Socks5Command.UdpAssociate);
        Assert.Equal((long)7, normalized.ScopeId);
    }

    [Fact]
    public void Socks5UdpDecodeCarriesRelayScopeForIpv6Address()
    {
        // M2: the SOCKS5 UDP wire format carries no scope, so TryDecode accepts an explicit scope
        // and reconstructs an IPv6 destination with a non-zero ScopeId.
        var address = IPAddress.Parse("fe80::53");
        var encoded = Socks5UdpCodec.Encode(address, 53, [1, 2, 3]);
        Assert.True(Socks5UdpCodec.TryDecode(encoded, out var decoded, scopeId: 9));
        Assert.NotNull(decoded.DestinationAddress);
        Assert.Equal((uint)9, decoded.DestinationAddress!.ScopeId);
    }

    [Fact]
    public async Task Socks5ConnectAsyncHonorsAttemptCapOnSocketCreationFailure()
    {
        // L1: each candidate address is one attempt; a socket-creation failure is a failed attempt.
        // The global attempt cap stops the sequential loop, and exhausting candidates fails closed
        // (an exception surfaces as blocked) rather than hanging the capture path.
        var server = new Socks5Server("test", "host.invalid", 1080, null, null);
        var answers = new[]
        {
            IPAddress.Parse("192.0.2.1"), IPAddress.Parse("192.0.2.2"),
            IPAddress.Parse("192.0.2.3"), IPAddress.Parse("192.0.2.4")
        };
        var socketCreations = 0;

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await Socks5ControlConnection.ConnectAsync(
                server,
                CancellationToken.None,
                resolveAddresses: (_, _) => ValueTask.FromResult(answers),
                socketFactory: _ => { socketCreations++; throw new SocketException((int)SocketError.SocketError); },
                maxAttempts: 2);
        });

        // Only the capped number of attempts runs, never the full address list.
        Assert.Equal(2, socketCreations);
    }

    [Fact]
    public async Task Socks5ConnectAsyncDisposesEveryCreatedSocketOnFailure()
    {
        // L1: when every connect attempt fails, the sequential loop must dispose each socket it
        // created before advancing to the next candidate. A leaked socket would hold the local
        // ephemeral endpoint and a file descriptor. The attempt cap is also honored: only the
        // capped number of sockets are created, never the full address list.
        var server = new Socks5Server("test", "host.invalid", 1080, null, null);
        var candidates = new[]
        {
            IPAddress.Parse("127.0.0.1"), IPAddress.Parse("127.0.0.1"),
            IPAddress.Parse("127.0.0.1"), IPAddress.Parse("127.0.0.1")
        };
        var tracked = new List<TrackingSocket>();

        // Connect to a closed loopback port fails fast (ECONNREFUSED) so the attempt loop advances
        // without real network. All candidates fail -> an IOException surfaces (blocked).
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await Socks5ControlConnection.ConnectAsync(
                server,
                CancellationToken.None,
                resolveAddresses: (_, _) => ValueTask.FromResult(candidates),
                socketFactory: family =>
                {
                    var socket = new TrackingSocket(family);
                    lock (tracked) tracked.Add(socket);
                    return socket;
                },
                maxAttempts: 2);
        });

        Assert.Equal(2, tracked.Count);
        Assert.All(tracked, socket => Assert.True(socket.IsDisposedValue, "every created socket must be disposed on connect failure"));
    }

    // Tracks disposal through the virtual protected Dispose(bool) so the loop's socket cleanup is
    // observable without real sockets in flight.
    private sealed class TrackingSocket : System.Net.Sockets.Socket
    {
        public TrackingSocket(System.Net.Sockets.AddressFamily family) : base(family, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { }
        public bool IsDisposedValue { get; private set; }
        protected override void Dispose(bool disposing)
        {
            IsDisposedValue = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void Socks5SuccessPrefixIsAcceptedByPrefixParser()
    {
        // Regression: ReadEndpointReplyAsync reads a 5-byte prefix and must accept a SUCCESS
        // prefix (VER=5, REP=0) without requiring the full >= 8-byte reply. The previous code
        // called the full-reply parser on the prefix, which rejected every success reply with
        // "invalid reply prefix" (the bound address arrives in the second read).
        var success = new byte[] { 5, 0, 0, 1, 192 };
        Assert.True(Socks5Messages.TryParseReplyPrefix(success, out var okStatus));
        Assert.Equal((byte)0, okStatus);

        var failure = new byte[] { 5, 5, 0, 1, 192 };
        Assert.True(Socks5Messages.TryParseReplyPrefix(failure, out var refused));
        Assert.Equal((byte)5, refused);

        var badVersion = new byte[] { 4, 0, 0, 1, 192 };
        Assert.False(Socks5Messages.TryParseReplyPrefix(badVersion, out _));
    }

    [Fact]
    public void IPv4UdpPacketParserRejectsFragmentsAndReadsPayload()
    {
        var frame = new byte[14 + 20 + 8 + 3];
        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), 31);
        frame[23] = 17;
        IPAddress.Parse("192.0.2.10").GetAddressBytes().CopyTo(frame, 26);
        IPAddress.Parse("192.0.2.53").GetAddressBytes().CopyTo(frame, 30);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), 53000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(38, 2), 11);
        frame[42] = 1;
        frame[43] = 2;
        frame[44] = 3;

        Assert.True(IpUdpPacket.TryParse(frame, out var packet));
        Assert.Equal((ushort)53000, packet.SourcePort);
        Assert.Equal(new byte[] { 1, 2, 3 }, packet.Payload.ToArray());
        frame[20] = 0x20;
        Assert.False(IpUdpPacket.TryParse(frame, out _));
    }

    [Fact]
    public void IPv4UdpRewriteUpdatesEndpointsAndChecksums()
    {
        var frame = CreateIpv4UdpFrame();

        Assert.True(PacketChecksums.TryRewriteUdpEndpoints(frame, IPAddress.Parse("198.51.100.1"), 40000, IPAddress.Parse("203.0.113.2"), 5353));
        Assert.True(IpUdpPacket.TryParse(frame, out var packet));
        Assert.Equal(IPAddress.Parse("198.51.100.1"), packet.SourceAddress);
        Assert.Equal((ushort)40000, packet.SourcePort);
        Assert.Equal(IPAddress.Parse("203.0.113.2"), packet.DestinationAddress);
        Assert.Equal((ushort)5353, packet.DestinationPort);
        Assert.Equal((ushort)0, PacketChecksums.InternetChecksum(frame.AsSpan(14, 20)));
    }

    [Fact]
    public void UdpChecksumIsValidAfterEndpointRewrite()
    {
        var frame = CreateIpv4UdpFrame();
        Assert.True(PacketChecksums.TryRewriteUdpEndpoints(frame, IPAddress.Parse("198.51.100.1"), 40000, IPAddress.Parse("203.0.113.2"), 5353));
        var udpOffset = 14 + 20;
        var udpLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(udpOffset + 4, 2));
        var checksum = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(udpOffset + 6, 2));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 6, 2), 0);
        var source = frame.AsSpan(14 + 12, 4);
        var destination = frame.AsSpan(14 + 16, 4);
        var pseudo = new byte[12 + udpLength];
        source.CopyTo(pseudo);
        destination.CopyTo(pseudo.AsSpan(4));
        pseudo[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(pseudo.AsSpan(10, 2), udpLength);
        frame.AsSpan(udpOffset, udpLength).CopyTo(pseudo.AsSpan(12));
        Assert.Equal(checksum, PacketChecksums.InternetChecksum(pseudo));
    }

    [Fact]
    public void ConfigurationRejectsEmptyMatchAndProxyWithoutServer()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [{ "remotePort": [], "action": "proxy" }],
          "fallbackAction": "pass"
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "rules[0].remotePort", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "rules[0].proxyServer", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationRejectsWhitespaceProcessSelector()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [{ "process": ["   "], "action": "pass" }],
          "fallbackAction": "pass"
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "rules[0].process[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationRejectsDuplicateServerNamesCaseInsensitively()
    {
        const string json = """
        {
          "socks5Servers": [
            { "name": "Main", "host": "127.0.0.1", "port": 1080 },
            { "name": "main", "host": "127.0.0.2", "port": 1081 }
          ],
          "rules": [],
          "fallbackAction": "pass"
        }
        """;

        AssertInvalid(json, "socks5Servers[1].name");
    }

    [Fact]
    public void ConfigurationRejectsFallbackProxyAction()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "proxy"
        }
        """;

        AssertInvalid(json, "fallbackAction");
    }

    [Fact]
    public void ConfigurationRejectsUnknownJsonFields()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass",
          "unexpectedField": true
        }
        """;

        Assert.False(ConfigurationLoader.TryParse(json, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path.Contains("unexpectedField", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationParseDiagnosticsNameTheFailingFieldWithoutEchoingCredentials()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass",
          "unexpectedField": "credential-that-must-not-appear"
        }
        """;

        Assert.False(ConfigurationLoader.TryParse(json, out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("unexpectedField", diagnostic.Path, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-that-must-not-appear", diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationRejectsOutOfRangePort()
    {
        const string json = """
        {
          "socks5Servers": [ { "name": "Main", "host": "127.0.0.1", "port": 0 } ],
          "rules": [],
          "fallbackAction": "pass"
        }
        """;

        AssertInvalid(json, "socks5Servers[0].port");
    }

    [Fact]
    public void ConfigurationRejectsProxyServerOnNonProxyRule()
    {
        const string json = """
        {
          "socks5Servers": [ { "name": "Main", "host": "127.0.0.1", "port": 1080 } ],
          "rules": [ { "action": "pass", "proxyServer": "Main" } ],
          "fallbackAction": "pass"
        }
        """;

        AssertInvalid(json, "rules[0].proxyServer");
    }

    [Fact]
    public void ConfigurationRejectsNonBlockFailureAction()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [],
          "fallbackAction": "pass",
          "proxyUnavailableAction": "pass"
        }
        """;

        AssertInvalid(json, "proxyUnavailableAction");
    }

    [Fact]
    public void ConfigurationRejectsUnpairedUsername()
    {
        const string json = """
        {
          "socks5Servers": [ { "name": "Main", "host": "127.0.0.1", "port": 1080, "username": "user" } ],
          "rules": [],
          "fallbackAction": "pass"
        }
        """;

        AssertInvalid(json, "socks5Servers[0]");
    }

    [Fact]
    public void ConfigurationEnforcesUtf8CredentialLengthWithoutDisclosingPassword()
    {
        var maximumPassword = new string('a', 255);
        var maximumJson = $$"""
        {
          "socks5Servers": [ { "name": "Main", "host": "127.0.0.1", "port": 1080, "username": "user", "password": "{{maximumPassword}}" } ],
          "rules": [],
          "fallbackAction": "pass"
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(maximumJson, out var maximumDto, out _));
        Assert.NotNull(maximumDto);
        Assert.True(ConfigurationLoader.TryValidate(maximumDto!, out _, out var maximumDiagnostics), string.Join("; ", maximumDiagnostics));

        var password = new string('\u00e9', 128);
        var json = $$"""
        {
          "socks5Servers": [ { "name": "Main", "host": "127.0.0.1", "port": 1080, "username": "user", "password": "{{password}}" } ],
          "rules": [],
          "fallbackAction": "pass"
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("socks5Servers[0].password", diagnostic.Path);
        Assert.DoesNotContain(password, diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationRejectsInvalidHost()
    {
        const string json = """
        {
          "socks5Servers": [ { "name": "Main", "host": "not a host name!", "port": 1080 } ],
          "rules": [],
          "fallbackAction": "pass"
        }
        """;

        AssertInvalid(json, "socks5Servers[0].host");
    }

    [Fact]
    public void ConfigurationRequiresFallbackAndSectionFields()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": []
        }
        """;

        AssertInvalid(json, "fallbackAction");

        const string missingSections = """
        {}
        """;
        Assert.True(ConfigurationLoader.TryParse(missingSections, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "socks5Servers", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "rules", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, "fallbackAction", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("adapterId")]
    [InlineData("adapterName")]
    [InlineData("protocol")]
    [InlineData("addressFamily")]
    [InlineData("remoteCidr")]
    [InlineData("remotePort")]
    public void ConfigurationRejectsNullRuleMatchValuesWithoutThrowing(string field)
    {
        var json = $$"""
        {
          "socks5Servers": [],
          "rules": [{ "{{field}}": [null], "action": "pass" }],
          "fallbackAction": "pass"
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, $"rules[0].{field}[0]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("socks5Servers")]
    [InlineData("rules")]
    public void ConfigurationRejectsNullSectionEntriesWithoutThrowing(string section)
    {
        var json = $$"""
        {
          "socks5Servers": {{(string.Equals(section, "socks5Servers", StringComparison.Ordinal) ? "[null]" : "[]")}},
          "rules": {{(string.Equals(section, "rules", StringComparison.Ordinal) ? "[null]" : "[]")}},
          "fallbackAction": "pass"
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, $"{section}[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationNormalizesAndMergesRemotePortRanges()
    {
        const string json = """
        {
          "socks5Servers": [],
          "rules": [{ "remotePort": ["443", "100-200", "80-150", "201-250", "443"], "action": "pass" }],
          "fallbackAction": "pass"
        }
        """;

        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.True(ConfigurationLoader.TryValidate(dto!, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.NotNull(configuration);
        Assert.Equal(new[] { ((ushort)80, (ushort)250), ((ushort)443, (ushort)443) }, configuration!.Policy.Rules[0].Matcher.RemotePorts);
    }

    [Fact]
    public void PolicyAndAcrossFieldsRequiresEveryPopulatedField()
    {
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 80), TransportProtocol.Udp, FlowOriginKind.Host);
        var context = new FlowContext(key, "dns.exe", null, null, null, 80);
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns.exe" }, RemotePorts: [(53, 53)]), new FlowDecision(FlowAction.Block, 0, null))],
            FlowAction.Pass);

        // Process matches but the remote port does not -> the AND rule must not match.
        Assert.Equal(FlowAction.Pass, policy.Evaluate(context).Action);

        var matchingContext = context with { RemotePort = 53 };
        Assert.Equal(FlowAction.Block, policy.Evaluate(matchingContext).Action);
    }

    [Fact]
    public void PolicyAlternativesWithinFieldUseOrSemantics()
    {
        var key = FlowKey.Create(Endpoint.From(IPAddress.Loopback, 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Udp, FlowOriginKind.Host);
        var context = new FlowContext(key, "dns.exe", null, null, null, 443);
        var policy = new PolicySnapshot(
            [new(new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "other.exe", "dns.exe" }, RemotePorts: [(80, 80), (443, 443)]), new FlowDecision(FlowAction.Block, 0, null))],
            FlowAction.Pass);

        // Matches the second process alternative AND the second port range.
        Assert.Equal(FlowAction.Block, policy.Evaluate(context).Action);
    }

    [Fact]
    public void IPv6UdpPacketParserReadsAddressesAndPayload()
    {
        var frame = CreateIpv6UdpFrame();

        Assert.True(IpUdpPacket.TryParse(frame, out var packet));
        Assert.Equal(IPAddress.Parse("2001:db8::10"), packet.SourceAddress);
        Assert.Equal(IPAddress.Parse("2001:db8::53"), packet.DestinationAddress);
        Assert.Equal((ushort)53000, packet.SourcePort);
        Assert.Equal((ushort)53, packet.DestinationPort);
        Assert.Equal(new byte[] { 1, 2, 3 }, packet.Payload.ToArray());
    }

    [Fact]
    public void IPv6UdpRewriteUpdatesEndpointsAndChecksum()
    {
        var frame = CreateIpv6UdpFrame();

        Assert.True(PacketChecksums.TryRewriteUdpEndpoints(frame, IPAddress.Parse("2001:db8::99"), 40000, IPAddress.Parse("2001:db8::1"), 5353));
        Assert.True(IpUdpPacket.TryParse(frame, out var packet));
        Assert.Equal(IPAddress.Parse("2001:db8::99"), packet.SourceAddress);
        Assert.Equal((ushort)40000, packet.SourcePort);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), packet.DestinationAddress);
        Assert.Equal((ushort)5353, packet.DestinationPort);

        var udpOffset = 14 + 40;
        var udpLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(udpOffset + 4, 2));
        var checksum = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(udpOffset + 6, 2));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 6, 2), 0);
        var pseudo = new byte[40 + udpLength];
        frame.AsSpan(14 + 8, 16).CopyTo(pseudo);
        frame.AsSpan(14 + 24, 16).CopyTo(pseudo.AsSpan(16));
        BinaryPrimitives.WriteUInt32BigEndian(pseudo.AsSpan(32, 4), udpLength);
        pseudo[39] = 17;
        frame.AsSpan(udpOffset, udpLength).CopyTo(pseudo.AsSpan(40));
        Assert.NotEqual((ushort)0, checksum);
        Assert.Equal(checksum, PacketChecksums.InternetChecksum(pseudo));
    }

    [Fact]
    public void IPv6UdpParserRejectsFragmentHeader()
    {
        var frame = CreateIpv6UdpFrame();
        frame[20] = 44; // next header = fragment

        Assert.False(IpUdpPacket.TryParse(frame, out _));
    }

    private static void AssertInvalid(string json, params string[] expectedPaths)
    {
        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out _));
        Assert.NotNull(dto);
        Assert.False(ConfigurationLoader.TryValidate(dto!, out _, out var diagnostics));
        foreach (var path in expectedPaths) Assert.Contains(diagnostics, diagnostic => string.Equals(diagnostic.Path, path, StringComparison.Ordinal));
    }

    private static byte[] CreateIpv6UdpFrame()
    {
        var frame = new byte[14 + 40 + 8 + 3];
        frame[12] = 0x86;
        frame[13] = 0xdd;
        frame[14] = 0x60;
        frame[18] = 0;
        frame[19] = 11; // payload length = UDP(8) + payload(3)
        frame[20] = 17; // next header = UDP
        frame[21] = 64; // hop limit
        IPAddress.Parse("2001:db8::10").GetAddressBytes().CopyTo(frame, 22);
        IPAddress.Parse("2001:db8::53").GetAddressBytes().CopyTo(frame, 38);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(54, 2), 53000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(56, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(58, 2), 11);
        frame[62] = 1;
        frame[63] = 2;
        frame[64] = 3;
        return frame;
    }

    private static byte[] CreateIpv4UdpFrame()
    {
        var frame = new byte[14 + 20 + 8 + 3];
        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), 31);
        frame[23] = 17;
        IPAddress.Parse("192.0.2.10").GetAddressBytes().CopyTo(frame, 26);
        IPAddress.Parse("192.0.2.53").GetAddressBytes().CopyTo(frame, 30);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), 53000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(38, 2), 11);
        frame[42] = 1;
        frame[43] = 2;
        frame[44] = 3;
        return frame;
    }

    [Fact]
    public void ExampleConfigurationsAllValidate()
    {
        // The examples/ directory is published documentation; every example must be a valid
        // configuration so operators can copy them without surprises.
        var exampleDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "examples");
        if (!Directory.Exists(exampleDir)) return; // Source tree layout differs in some build hosts.
        var files = Directory.GetFiles(exampleDir, "*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var json = File.ReadAllText(file);
            Assert.True(ConfigurationLoader.TryParse(json, out var dto, out var parseErrors), $"{Path.GetFileName(file)} parse: {string.Join("; ", parseErrors)}");
            Assert.True(ConfigurationLoader.TryValidate(dto!, out _, out var validationErrors), $"{Path.GetFileName(file)} validate: {string.Join("; ", validationErrors)}");
        }
    }

}
