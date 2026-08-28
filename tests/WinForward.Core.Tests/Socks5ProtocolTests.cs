using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Protocols;
using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class Socks5ProtocolTests
{
    [Fact]
    public void Socks5UsernamePasswordAndGreetingUseRfcLengths()
    {
        Assert.Equal(new byte[] { 5, 1, 0 }, Socks5Messages.Greeting(credentials: false));
        Assert.Equal(new byte[] { 5, 2, 0, 2 }, Socks5Messages.Greeting(credentials: true));
        Assert.Equal(new byte[] { 1, 1, (byte)'u', 1, (byte)'p' }, Socks5Messages.UsernamePassword("u", "p"));
    }

    [Fact]
    public void Socks5UsernamePasswordAllowsEmptySecret()
    {
        // R4: RFC 1929 permits a zero-length password; the encoded message carries a 0-length field.
        Assert.Equal(new byte[] { 1, 4, (byte)'u', (byte)'s', (byte)'e', (byte)'r', 0 }, Socks5Messages.UsernamePassword("user", ""));
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
                    var socket = new TrackingSocket(family, SocketType.Stream, ProtocolType.Tcp);
                    lock (tracked) tracked.Add(socket);
                    return socket;
                },
                maxAttempts: 2);
        });

        Assert.Equal(2, tracked.Count);
        Assert.All(tracked, socket => Assert.True(socket.IsDisposedValue, "every created socket must be disposed on connect failure"));
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
}
