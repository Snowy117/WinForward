using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Runtime.Socks5.Tests;

public sealed class Socks5ProtocolTests
{
    [Fact]
    public void Socks5UsernamePasswordAndGreetingUseRfcLengths()
    {
        Assert.Equal(new byte[] { 5, 1, 0 }, Socks5Messages.Greeting(credentials: false));
        Assert.Equal(new byte[] { 5, 2, 0, 2 }, Socks5Messages.Greeting(credentials: true));
        var credentials = new byte[Socks5Messages.UsernamePasswordLength("u", "p")];
        _ = Socks5Messages.WriteUsernamePassword("u", "p", credentials);
        Assert.Equal(new byte[] { 1, 1, (byte)'u', 1, (byte)'p' }, credentials);
    }

    [Fact]
    public void Socks5UsernamePasswordAllowsEmptySecret()
    {
        // RFC 1929 permits a zero-length password; the encoded message carries a 0-length field.
        var credentials = new byte[Socks5Messages.UsernamePasswordLength("user", "")];
        _ = Socks5Messages.WriteUsernamePassword("user", "", credentials);
        Assert.Equal(new byte[] { 1, 4, (byte)'u', (byte)'s', (byte)'e', (byte)'r', 0 }, credentials);
    }

    [Fact]
    public void Socks5RequestsCarryCommandAddressAndPort()
    {
        Span<byte> scratch = stackalloc byte[Socks5Messages.RequestLength(IPAddress.Parse("192.0.2.53"))];
        var length = Socks5Messages.WriteRequest(Socks5Command.UdpAssociate, IPAddress.Parse("192.0.2.53"), 5353, scratch);
        var request = scratch[..length].ToArray();

        Assert.Equal(new byte[] { 5, 3, 0, 1, 192, 0, 2, 53, 0x14, 0xe9 }, request);
    }

    [Fact]
    public void Socks5DomainRequestCarriesAtyp3LengthPrefixedNameAndPort()
    {
        const string magic = "sp.v2.udp-over-tcp.arpa";
        Span<byte> scratch = stackalloc byte[Socks5Messages.RequestLength(magic)];
        var length = Socks5Messages.WriteRequest(Socks5Command.Connect, magic, 0, scratch);
        var request = scratch[..length].ToArray();

        Assert.Equal(30, length);
        Assert.Equal(new byte[] { 5, 1, 0, 3, 23 }, request[..5]);
        Assert.Equal("sp.v2.udp-over-tcp.arpa"u8.ToArray(), request[5..28]);
        // ReSharper disable once UseUtf8StringLiteral // The expectation is two literal zero bytes (the request's port); a UTF-8 literal would spell the same expectation as escape sequences.
        Assert.Equal(new byte[] { 0, 0 }, request[28..]);
    }

    [Fact]
    public void Socks5DomainRequestLengthAgreesWithTheWrittenBytes()
    {
        const string host = "example.com";
        Span<byte> scratch = stackalloc byte[Socks5Messages.RequestLength(host)];
        var length = Socks5Messages.WriteRequest(Socks5Command.UdpAssociate, host, 5353, scratch);
        var request = scratch[..length].ToArray();

        Assert.Equal(Socks5Messages.RequestLength(host), length);
        Assert.Equal(new byte[] { 5, 3, 0, 3, 11 }, request[..5]);
        Assert.Equal("example.com"u8.ToArray(), request[5..16]);
        Assert.Equal(new byte[] { 0x14, 0xe9 }, request[16..]);
    }

    [Fact]
    public void Socks5DomainRequestGuardsTheNameLengthAndTheDestinationSpan()
    {
        // RFC 1928 carries the name length in one byte: 255 is the largest legal name, and an empty
        // name is malformed rather than a zero-length request.
        Assert.Equal(4 + 1 + 255 + 2, Socks5Messages.RequestLength(new string('a', 255)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Socks5Messages.RequestLength(string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => Socks5Messages.RequestLength(new string('a', 256)));
        Assert.Throws<ArgumentException>(() =>
        {
            Span<byte> tooSmall = stackalloc byte[Socks5Messages.RequestLength("example.com") - 1];
            _ = Socks5Messages.WriteRequest(Socks5Command.Connect, "example.com", 0, tooSmall);
        });
    }

    [Fact]
    public void UotRequestHeaderUsesTheSocksAddressTypesForBothIpFamilies()
    {
        // UoT v2's request destination is an ordinary SOCKS address (the intercepting server reads it
        // with its SOCKS address serializer), so the address types are RFC 1928's 1/4 — the same ones
        // this repository's SOCKS5 encoder writes — and not the per-datagram 0x00/0x01 of protocol
        // version 1's stream format.
        Span<byte> scratch = stackalloc byte[UotCodec.MaximumRequestHeaderLength];
        var ipv4 = IPAddressValue.From(IPAddress.Parse("192.0.2.53"));

        Assert.True(UotCodec.TryWriteRequestHeader(isConnect: true, ipv4, 5353, scratch, out var ipv4Written));
        Assert.Equal(UotCodec.RequestHeaderLength(AddressFamilyKind.IPv4), ipv4Written);
        Assert.Equal(new byte[] { 1, 1, 192, 0, 2, 53, 0x14, 0xe9 }, scratch[..ipv4Written].ToArray());
        Assert.Equal(Socks5Messages.AddressTypeIPv4, scratch[1]);

        // The isConnect byte is the request's mode, and connect mode is the only mode this codec
        // frames: a non-connect request asks for the v1 per-datagram stream format, so it is refused
        // instead of written with types this codec does not carry.
        scratch.Clear();
        Assert.False(UotCodec.TryWriteRequestHeader(isConnect: false, ipv4, 5353, scratch, out var notConnectWritten));
        Assert.Equal(0, notConnectWritten);
        Assert.Equal(new byte[UotCodec.MaximumRequestHeaderLength], scratch.ToArray());

        var ipv6 = IPAddressValue.From(IPAddress.Parse("2001:db8::53"));
        Assert.True(UotCodec.TryWriteRequestHeader(isConnect: true, ipv6, 5353, scratch, out var ipv6Written));
        Assert.Equal(UotCodec.RequestHeaderLength(AddressFamilyKind.IPv6), ipv6Written);
        Assert.Equal(20, ipv6Written);
        Assert.Equal(
            new byte[]
            {
                1, 4,
                0x20, 0x01, 0x0d, 0xb8, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x53,
                0x14, 0xe9,
            },
            scratch[..ipv6Written].ToArray());
        Assert.Equal(Socks5Messages.AddressTypeIPv6, scratch[1]);
    }

    [Fact]
    public void UotRequestHeaderDestinationDecodesThroughTheSocksReplyParser()
    {
        // The structural guard against a second ATYP divergence: the header is decoded by
        // Socks5Messages.TryParseReply — an independent SOCKS5 decoder, not UotCodec's own reader.
        // The written destination is wrapped as a success reply (VER 5 | REP 0 | RSV 0 | ATYP |
        // address | port), so only an RFC 1928 address type can pass: the per-datagram format's
        // 0x00 IPv4 is an unknown family (Invalid), and its 0x01 IPv6 makes the parser read the IPv6
        // address as a four-byte IPv4 address and the next two address bytes as the port, failing the
        // address and port assertions below.
        Span<byte> scratch = stackalloc byte[UotCodec.MaximumRequestHeaderLength];
        Span<byte> reply = stackalloc byte[3 + UotCodec.MaximumRequestHeaderLength - 1];
        foreach (var (address, addressType) in new[]
        {
            (IPAddress.Parse("192.0.2.53"), Socks5Messages.AddressTypeIPv4),
            (IPAddress.Parse("2001:db8::53"), Socks5Messages.AddressTypeIPv6),
        })
        {
            Assert.True(UotCodec.TryWriteRequestHeader(isConnect: true, IPAddressValue.From(address), 5353, scratch, out var written));
            var header = scratch[..written];
            Assert.Equal((byte)1, header[0]);

            reply[0] = 5;
            reply[1] = 0;
            reply[2] = 0;
            header[1..].CopyTo(reply[3..]);
            var framed = reply[..(3 + header.Length - 1)];

            Assert.Equal(Socks5ReplyKind.Success, Socks5Messages.TryParseReply(framed, out var status, out var parsedType, out var port));
            Assert.Equal((byte)0, status);
            Assert.Equal(addressType, parsedType);
            Assert.Equal((ushort)5353, port);
            Assert.Equal(address.GetAddressBytes(), framed.Slice(4, addressType == Socks5Messages.AddressTypeIPv4 ? 4 : 16).ToArray());
            Assert.Equal(2 + (addressType == Socks5Messages.AddressTypeIPv4 ? 4 : 16) + 2, written);
        }
    }

    [Fact]
    public void UotRequestHeaderWriterFailsClosedOnATooSmallDestination()
    {
        Assert.Equal(UotCodec.MaximumRequestHeaderLength, UotCodec.RequestHeaderLength(AddressFamilyKind.IPv6));
        var ipv6 = IPAddressValue.From(IPAddress.Parse("2001:db8::53"));

        Span<byte> oneByteShort = stackalloc byte[UotCodec.MaximumRequestHeaderLength - 1];
        Assert.False(UotCodec.TryWriteRequestHeader(isConnect: true, ipv6, 0, oneByteShort, out var written));
        Assert.Equal(0, written);

        Span<byte> exact = stackalloc byte[UotCodec.MaximumRequestHeaderLength];
        Assert.True(UotCodec.TryWriteRequestHeader(isConnect: true, ipv6, 0, exact, out written));
        Assert.Equal(exact.Length, written);

        // The IPv4 header fits where the IPv6 header does not.
        Assert.True(UotCodec.TryWriteRequestHeader(isConnect: true, IPAddressValue.From(IPAddress.Parse("192.0.2.53")), 0, oneByteShort, out written));
        Assert.Equal(UotCodec.RequestHeaderLength(AddressFamilyKind.IPv4), written);
    }

    [Fact]
    public void UotFramePrefixIsThePayloadLengthInNetworkOrder()
    {
        Assert.Equal(2, UotCodec.FrameHeaderSize);
        Span<byte> scratch = stackalloc byte[UotCodec.FrameHeaderSize];

        Assert.True(UotCodec.TryWriteFrameHeader(0x0201, scratch, out var written));
        Assert.Equal(UotCodec.FrameHeaderSize, written);
        Assert.Equal(new byte[] { 0x02, 0x01 }, scratch.ToArray());

        Assert.True(UotCodec.TryWriteFrameHeader(ushort.MaxValue, scratch, out written));
        Assert.Equal(new byte[] { 0xff, 0xff }, scratch.ToArray());

        // A zero-length frame is a legal empty datagram, not a terminator.
        Assert.True(UotCodec.TryWriteFrameHeader(0, scratch, out written));
        // ReSharper disable once UseUtf8StringLiteral // The expectation is two literal zero bytes (the frame's length prefix); a UTF-8 literal would spell the same expectation as escape sequences.
        Assert.Equal(new byte[] { 0, 0 }, scratch.ToArray());
    }

    [Fact]
    public void UotFrameHeaderWriterFailsClosedOnATooSmallDestination()
    {
        Span<byte> oneByteShort = stackalloc byte[UotCodec.FrameHeaderSize - 1];
        Assert.False(UotCodec.TryWriteFrameHeader(1, oneByteShort, out var written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void UotMagicAddressIsTheV2ArpaName()
    {
        // The server dispatches UoT on this exact FQDN and the protocol version lives in its "v2"
        // segment: a typo falls through to a plain CONNECT to a name that does not resolve.
        Assert.Equal("sp.v2.udp-over-tcp.arpa", UotCodec.MagicAddress);
        Assert.Equal(2, UotCodec.Version);
        Assert.Contains("v2", UotCodec.MagicAddress, StringComparison.Ordinal);
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
        // A failure reply (REP 1-8) must be reported as a definite Failure without a bound
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
        // A UDP ASSOCIATE reply returning 0.0.0.0/:: is substituted with the TCP control
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
        // A CONNECT reply's BND.ADDR is not used for routing, so an unspecified/mapped reply
        // must NOT be substituted with the control peer.
        var mappedAny = IPAddress.Parse("::ffff:0.0.0.0");
        var normalized = Socks5Messages.NormalizeBndAddress(mappedAny, IPAddress.Parse("192.0.2.100"), Socks5Command.Connect);
        Assert.Equal(IPAddress.Any, normalized);
    }

    [Fact]
    public void NormalizeBndAddressMapsIPv4MappedToPlainIPv4()
    {
        // An IPv4-mapped ::ffff:a.b.c.d reply is normalized to its IPv4 form.
        var mapped = IPAddress.Parse("::ffff:192.0.2.53");
        var controlPeer = IPAddress.Parse("192.0.2.100");
        var normalized = Socks5Messages.NormalizeBndAddress(mapped, controlPeer, Socks5Command.UdpAssociate);
        Assert.Equal(IPAddress.Parse("192.0.2.53"), normalized);
    }

    [Fact]
    public void NormalizeBndAddressCarriesControlPeerScopeOverIPv6Reply()
    {
        // A genuine IPv6 relay address reconstructed from raw bytes has ScopeId 0; it inherits
        // the control peer's non-zero interface scope so a link-local relay routes correctly.
        var linkLocal = new IPAddress(IPAddress.Parse("fe80::1").GetAddressBytes(), 0);
        var scopeSource = new IPAddress(IPAddress.Parse("fe80::10").GetAddressBytes(), 7);
        var normalized = Socks5Messages.NormalizeBndAddress(linkLocal, scopeSource, Socks5Command.UdpAssociate);
        Assert.Equal(7, normalized.ScopeId);
    }

    [Fact]
    public async Task Socks5ConnectAsyncHonorsAttemptCapOnSocketCreationFailure()
    {
        // Each candidate address is one attempt; a socket-creation failure is a failed attempt.
        // The global attempt cap stops the sequential loop, and exhausting candidates fails closed
        // (an exception surfaces as blocked) rather than hanging the capture path.
        var server = new Socks5Server("test", "host.invalid", 1080, Username: null, Password: null);
        var answers = new[]
        {
            IPAddress.Parse("192.0.2.1"), IPAddress.Parse("192.0.2.2"),
            IPAddress.Parse("192.0.2.3"), IPAddress.Parse("192.0.2.4"),
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
        // When every connect attempt fails, the sequential loop must dispose each socket it
        // created before advancing to the next candidate. A leaked socket would hold the local
        // ephemeral endpoint and a file descriptor. The attempt cap is also honored: only the
        // capped number of sockets are created, never the full address list.
        var server = new Socks5Server("test", "host.invalid", 1080, Username: null, Password: null);
        var candidates = new[]
        {
            IPAddress.Parse("127.0.0.1"), IPAddress.Parse("127.0.0.1"),
            IPAddress.Parse("127.0.0.1"), IPAddress.Parse("127.0.0.1"),
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
