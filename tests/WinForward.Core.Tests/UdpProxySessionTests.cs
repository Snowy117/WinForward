using System.Net;
using System.Reflection;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
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
    private static readonly NativeBufferPool s_receiveWindowPool = new(1537);

    [Fact]
    public async Task FirstActivityPropagatesToTheAssociationTableImmediately()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var stamps = new List<DateTimeOffset>();
        var session = CreateSession(time, stamps);
        await using (session)
        {
            await session.SendSpanAsync(session.Flow.Remote, [1], CancellationToken.None);
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
            await session.SendSpanAsync(session.Flow.Remote, [1], CancellationToken.None);
            Assert.Single(stamps);

            // Sends inside the interval keep the session timestamp exact but do not propagate.
            for (var index = 0; index < 5; index++)
            {
                time.Advance(TimeSpan.FromMilliseconds(10));
                await session.SendSpanAsync(session.Flow.Remote, [2], CancellationToken.None);
            }

            Assert.Single(stamps);
            Assert.Equal(time.GetUtcNow(), session.LastActivityUtc);

            // Crossing the interval lets exactly the next send propagate.
            time.Advance(TimeSpan.FromMilliseconds(60));
            await session.SendSpanAsync(session.Flow.Remote, [3], CancellationToken.None);
            Assert.Equal(2, stamps.Count);
            Assert.Equal(time.GetUtcNow(), session.LastActivityUtc);
            Assert.Equal(time.GetUtcNow(), stamps[1]);
        }
    }

    [Fact]
    public async Task SendSpanAsyncForwardsTheSessionEndpointToTheTransportUnchanged()
    {
        // R1: the session hands its Endpoint struct straight through to the transport — no
        // IPEndPoint round-trip on the forward leg — so address, port, and family arrive intact.
        var transport = new FakeTransport(System.Net.Sockets.AddressFamily.InterNetwork, 40000);
        var session = CreateSession(new MutableTimeProvider(DateTimeOffset.UnixEpoch), [], transport);
        await using (session)
        {
            await session.SendSpanAsync(session.Flow.Remote, [1], CancellationToken.None);
        }

        (Endpoint Destination, byte[] Payload) sent;
        lock (transport.Sent) sent = Assert.Single(transport.Sent);
        Assert.Equal(Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), sent.Destination);
        Assert.Equal(AddressFamilyKind.IPv4, sent.Destination.AddressFamily);
        Assert.Equal((ushort)53, sent.Destination.Port);
    }

    [Fact]
    public async Task IdleExpiryEndsTheReceiveLoopWithoutRecordingAFailure()
    {
        // R3: the receive loop reads through the session lifetime token, so an admitted idle
        // expiry cancels it as normal teardown — no receive failure is recorded and the
        // coordinator's failure handler stays untouched.
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var failureHandlerCalls = 0;
        var session = CreateSession(time, [], new FakeTransport(System.Net.Sockets.AddressFamily.InterNetwork, 40000));
        await using (session)
        {
            session.Start(_ => Interlocked.Increment(ref failureHandlerCalls));

            Assert.True(session.TryBeginExpiry(time.GetUtcNow(), TimeSpan.Zero));
            Assert.Equal(UdpSessionState.Expiring, session.State);
            // The expiry-admitted session refuses the send instead of throwing at the dispatcher.
            Assert.False(await session.SendSpanAsync(session.Flow.Remote, [1], CancellationToken.None));

            await session.DisposeAsync();
        }

        Assert.Equal(UdpSessionState.Disposed, session.State);
        Assert.Equal(0, failureHandlerCalls);
    }

    [Fact]
    public async Task GenuineReceiveFaultRecordsFaultedAndFiresTheFailureHandler()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new FakeTransport(System.Net.Sockets.AddressFamily.InterNetwork, 40000);
        var handled = new TaskCompletionSource<UdpProxySession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = CreateSession(time, [], transport);
        await using (session)
        {
            session.Start(faulted => handled.TrySetResult(faulted));

            transport.Received.Writer.TryComplete(new IOException("relay read failed"));

            Assert.Same(session, await handled.Task.WaitAsync(TimeSpan.FromSeconds(2), TimeProvider.System));
            Assert.Equal(UdpSessionState.Faulted, session.State);
            // A faulted session refuses further sends so the caller can fail the datagram closed.
            Assert.False(await session.SendSpanAsync(session.Flow.Remote, [1], CancellationToken.None));
        }
    }

    [Fact]
    public async Task DisposeAsyncWaitsForAnOutstandingSendLease()
    {
        // Per-owner quiescence (F1): the send admission holds a scope lease for the whole transport
        // await, so disposal must not return while the send is still in flight. The gate keeps the
        // send outstanding; the pending dispose is the discriminator (a send that did not hold a
        // lease would let disposal complete immediately).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(System.Net.Sockets.AddressFamily.InterNetwork, 40000) { SendGate = gate };
        var session = CreateSession(new MutableTimeProvider(DateTimeOffset.UnixEpoch), [], transport);

        var send = SendAsync(session, 1);
        Assert.False(send.IsCompleted);

        var dispose = DisposeSessionAsync(session);
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);

        gate.SetResult();
        Assert.True(await send.WaitAsync(TimeSpan.FromSeconds(5)));
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(UdpSessionState.Disposed, session.State);
    }

    private static async Task<bool> SendAsync(UdpProxySession session, byte value) =>
        await session.SendSpanAsync(session.Flow.Remote, [value], CancellationToken.None).ConfigureAwait(false);

    private static async Task DisposeSessionAsync(UdpProxySession session) =>
        await session.DisposeAsync().ConfigureAwait(false);

    [Fact]
    public void FaultedSessionStateHasNoParallelReceiveFailureField()
    {
        // D-C3-5: the session's single failure representation is the scope's Fault. A reintroduced
        // _receiveFailure field would be a second source of truth that can disagree with State and
        // the send admission (both read scope.Fault under _activityGate).
        Assert.Null(typeof(UdpProxySession).GetField("_receiveFailure", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void SessionContextStaysAValueTypeSoConstructionDoesNotAllocate()
    {
        // B (probe C2a1): the context is copied into the session constructor and never retained, so as
        // a record class it cost one heap allocation per session (240 B measured). The shape is load-
        // bearing for that saving; record value equality is unchanged by it.
        Assert.True(typeof(UdpProxySessionContext).IsValueType);
    }

    [Fact]
    public void ReceiveLoopIsASingleAsyncMethod()
    {
        // D: ReceiveLoopAsync + ReceiveDatagramsAsync boxed two state machines (and two Task objects)
        // per session for one loop. A reintroduced inner async method would silently pay the second
        // box again; the outer method called it exactly once, so there is no seam to preserve.
        Assert.Null(typeof(UdpProxySession).GetMethod("ReceiveDatagramsAsync", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    private static UdpProxySession CreateSession(TimeProvider time, List<DateTimeOffset> propagationStamps, IUdpProxyTransport? transport = null)
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
        return new UdpProxySession(new UdpProxySessionContext(
            flow,
            1,
            association,
            transport ?? new FakeTransport(System.Net.Sockets.AddressFamily.InterNetwork, 40000),
            new FakeResponseSink(),
            MacAddress.Invalid,
            time,
            (_, now) => propagationStamps.Add(now),
            NullRuntimeLogger.Instance,
            s_receiveWindowPool,
            1537,
            CancellationToken.None));
    }
}
