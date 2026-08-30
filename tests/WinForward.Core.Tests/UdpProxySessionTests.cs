using System.Buffers;
using System.Net;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.UdpProxy;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// D3 activity propagation: the association-table touch is throttled to one propagation per
/// interval (the first after any quieter-than-interval gap goes through immediately), while the
/// session's own <see cref="UdpProxySession.LastActivityUtc"/> stays exact per operation so
/// idle-expiry semantics are unchanged.
/// </summary>
public sealed class UdpProxySessionTests
{
    [Fact]
    public async Task FirstActivityPropagatesToTheAssociationTableImmediately()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var stamps = new List<DateTimeOffset>();
        var session = CreateSession(time, stamps);
        await using (session)
        {
            await session.SendAsync(session.Flow.Remote, new byte[] { 1 }, CancellationToken.None);
        }

        var stamp = Assert.Single(stamps);
        Assert.Equal(time.GetUtcNow(), stamp);
    }

    [Fact]
    public async Task RapidSendsPropagateAtMostOncePerInterval()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var stamps = new List<DateTimeOffset>();
        var session = CreateSession(time, stamps);
        await using (session)
        {
            await session.SendAsync(session.Flow.Remote, new byte[] { 1 }, CancellationToken.None);
            Assert.Single(stamps);

            // Sends inside the interval keep the session timestamp exact but do not propagate.
            for (var index = 0; index < 5; index++)
            {
                time.Advance(TimeSpan.FromMilliseconds(10));
                await session.SendAsync(session.Flow.Remote, new byte[] { 2 }, CancellationToken.None);
            }

            Assert.Single(stamps);
            Assert.Equal(time.GetUtcNow(), session.LastActivityUtc);

            // Crossing the interval lets exactly the next send propagate.
            time.Advance(TimeSpan.FromMilliseconds(60));
            await session.SendAsync(session.Flow.Remote, new byte[] { 3 }, CancellationToken.None);
            Assert.Equal(2, stamps.Count);
            Assert.Equal(time.GetUtcNow(), session.LastActivityUtc);
            Assert.Equal(time.GetUtcNow(), stamps[1]);
        }
    }

    [Fact]
    public async Task SendAsyncForwardsTheSessionEndpointToTheTransportUnchanged()
    {
        // R1: the session hands its Endpoint struct straight through to the transport — no
        // IPEndPoint round-trip on the forward leg — so address, port, and family arrive intact.
        var transport = new FakeTransport(System.Net.Sockets.AddressFamily.InterNetwork, 40000);
        var session = CreateSession(new MutableTimeProvider(DateTimeOffset.UnixEpoch), [], transport);
        await using (session)
        {
            await session.SendAsync(session.Flow.Remote, new byte[] { 1 }, CancellationToken.None);
        }

        (Endpoint Destination, byte[] Payload) sent;
        lock (transport.Sent) sent = Assert.Single(transport.Sent);
        Assert.Equal(Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), sent.Destination);
        Assert.Equal(AddressFamilyKind.IPv4, sent.Destination.AddressFamily);
        Assert.Equal((ushort)53, sent.Destination.Port);
    }

    private static UdpProxySession CreateSession(TimeProvider time, List<DateTimeOffset> propagationStamps, FakeTransport? transport = null)
    {
        var flow = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), 53),
            TransportProtocol.Udp,
            FlowOriginKind.Host);
        var association = new UdpAssociation(
            flow,
            new RelayAlias(FlowKey.Create(
                Endpoint.From(IPAddress.Loopback, 40000),
                Endpoint.From(IPAddress.Loopback, 50000),
                TransportProtocol.Udp,
                FlowOriginKind.Host)),
            1,
            time.GetUtcNow());
        return new UdpProxySession(
            flow,
            1,
            association,
            transport ?? new FakeTransport(System.Net.Sockets.AddressFamily.InterNetwork, 40000),
            new FakeResponseSink(),
            null,
            CancellationToken.None,
            time,
            (_, now) => propagationStamps.Add(now),
            NullRuntimeLogger.Instance,
            ArrayPool<byte>.Shared,
            1537);
    }
}
