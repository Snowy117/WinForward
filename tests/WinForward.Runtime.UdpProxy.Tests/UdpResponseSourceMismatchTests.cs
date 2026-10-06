using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.FlowBuilders;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// Reply-ownership observability: a relay response whose declared source is not the flow's own
/// destination is counted (once per reply) and warned once per window, while the reply itself keeps its
/// declared source and is delivered unchanged. The comparison is the peer address (port + address bits),
/// so an IPv6 scope that differs between the relay's receive interface and the captured flow is not a
/// mismatch, and one fact pins the counter's documented blind spot — two flows to the same destination
/// are indistinguishable by address, so a cross-delivered reply never advances it, and a zero count does
/// not mean association sharing is safe.
/// </summary>
public sealed class UdpResponseSourceMismatchTests
{
    private const string ForeignSourceEvent = "udp.response.foreign_source";

    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);
    private static readonly NativeBufferPool s_receiveWindowPool = new(1537);

    /// <summary>The shipped default verbosity: the warn is enabled, the trace per-packet event is not.</summary>
    private static RecordingLogger CreateInfoLevelLogger() =>
        new(static level => level >= LogLevel.Information);

    [Fact]
    public async Task ForeignSourceReplyIsCountedDeliveredAndKeepsTheSessionAlive()
    {
        var factory = new FakeTransportFactory();
        var sink = new FakeResponseSink();
        var logger = CreateInfoLevelLogger();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, sink, new UdpProxyOptions { Logger = logger });
        var flow = CreateFlow("192.0.2.53");
        var foreign = Endpoint.From(IPAddress.Parse("198.51.100.7"), 53);
        var before = RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch);

        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 1;
        });

        transport.EnqueueResponse(new UdpTransportDatagram(foreign.Address, SourceDomain: null, foreign.Port, new byte[] { 0x7f }));

        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (responseFlow, remoteSource, payload, _) = await sink.Responses.Reader.ReadAsync(readTimeout.Token);
        Assert.Equal(flow, responseFlow);
        // The declared source reaches the reinjector unchanged: the observation did not filter or rewrite.
        Assert.Equal(foreign, remoteSource);
        Assert.Equal(0x7f, Assert.Single(payload));
        Assert.Equal(1, RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch) - before);

        var warn = Assert.Single(logger.Events);
        Assert.Equal(ForeignSourceEvent, warn.Name);
        Assert.Equal(LogLevel.Warning, warn.Level);
        Assert.Equal(foreign.ToString(), Field(warn, "Source"));
        Assert.Equal(flow.Remote.ToString(), Field(warn, "Destination"));
        Assert.Equal(nameof(FlowOriginKind.Host), Field(warn, "Origin"));
        Assert.Equal("1", Field(warn, "UdpAssociation"));

        // The session survived the observation: the same relay carries another send.
        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [2], default, CancellationToken.None));
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 2;
        });
        Assert.False(transport.IsDisposed);
    }

    [Fact]
    public async Task MatchingReplyDoesNotMoveTheCounter()
    {
        var factory = new FakeTransportFactory();
        var sink = new FakeResponseSink();
        var logger = CreateInfoLevelLogger();
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, sink, new UdpProxyOptions { Logger = logger });
        var flow = CreateFlow("192.0.2.53");
        var before = RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch);

        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 1;
        });

        transport.EnqueueResponse(new UdpTransportDatagram(IPAddress.Parse("192.0.2.53"), SourceDomain: null, 53, new byte[] { 0x11 }));

        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (responseFlow, remoteSource, payload, _) = await sink.Responses.Reader.ReadAsync(readTimeout.Token);
        Assert.Equal(flow, responseFlow);
        Assert.Equal(flow.Remote, remoteSource);
        Assert.Equal(0x11, Assert.Single(payload));
        Assert.Equal(0, RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch) - before);
        Assert.DoesNotContain(logger.Events, static recorded => string.Equals(recorded.Name, ForeignSourceEvent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABurstOfForeignSourceRepliesCountsEveryOneAndWarnsOncePerWindow()
    {
        const int burst = 3;
        var factory = new FakeTransportFactory();
        var sink = new FakeResponseSink();
        var logger = CreateInfoLevelLogger();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, sink, new UdpProxyOptions { Logger = logger, TimeProvider = time });
        var flow = CreateFlow("192.0.2.53");
        var foreign = Endpoint.From(IPAddress.Parse("198.51.100.7"), 53);
        var before = RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch);

        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [1], default, CancellationToken.None));
        await WaitForAsync(() => factory.Transports.Count == 1);
        var transport = Assert.Single(factory.Transports);
        await WaitForAsync(() =>
        {
            lock (transport.Sent) return transport.Sent.Count == 1;
        });

        for (var index = 0; index < burst; index++)
        {
            transport.EnqueueResponse(new UdpTransportDatagram(foreign.Address, SourceDomain: null, foreign.Port, new[] { (byte)index }));
        }

        for (var index = 0; index < burst; index++)
        {
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var (_, remoteSource, payload, _) = await sink.Responses.Reader.ReadAsync(readTimeout.Token);
            Assert.Equal(foreign, remoteSource);
            Assert.Equal(index, Assert.Single(payload));
        }

        // Every reply inside the window was counted; only the first one was logged.
        Assert.Equal(burst, RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch) - before);
        Assert.Single(logger.Events, static recorded => string.Equals(recorded.Name, ForeignSourceEvent, StringComparison.Ordinal));

        // The next window logs again while the counter keeps accumulating.
        time.Advance(TimeSpan.FromSeconds(5));
        transport.EnqueueResponse(new UdpTransportDatagram(foreign.Address, SourceDomain: null, foreign.Port, new byte[] { 0x7f }));
        using (var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var (_, remoteSource, _, _) = await sink.Responses.Reader.ReadAsync(readTimeout.Token);
            Assert.Equal(foreign, remoteSource);
        }

        Assert.Equal(burst + 1, RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch) - before);
        Assert.Equal(2, logger.Events.Count(static recorded => string.Equals(recorded.Name, ForeignSourceEvent, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SameDestinationMisdeliveryIsInvisibleToTheCounter()
    {
        // The documented blind spot: A's reply arriving on B's session carries the source both flows
        // expect, because both target the same destination. No address comparison can separate them, so
        // the counter stays still while the reply is delivered as B's own.
        var sink = new FakeResponseSink();
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var flowA = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53001), destination, TransportProtocol.Udp, FlowOriginKind.Host);
        var flowB = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53002), destination, TransportProtocol.Udp, FlowOriginKind.Host);
        var transportB = new FakeTransport(AddressFamily.InterNetwork, 40011);
        await using var sessionA = CreateSession(flowA, new FakeTransport(AddressFamily.InterNetwork, 40010), sink);
        await using var sessionB = CreateSession(flowB, transportB, sink);
        sessionA.Start(static _ => { });
        sessionB.Start(static _ => { });

        Assert.True(await sessionA.SendSpanAsync(destination, [0xa1], CancellationToken.None));
        Assert.True(await sessionB.SendSpanAsync(destination, [0xb2], CancellationToken.None));
        var before = RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch);

        transportB.EnqueueResponse(new UdpTransportDatagram(IPAddress.Parse("192.0.2.53"), SourceDomain: null, 53, new byte[] { 0xc3 }));

        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (responseFlow, remoteSource, payload, _) = await sink.Responses.Reader.ReadAsync(readTimeout.Token);
        Assert.Equal(flowB, responseFlow);
        Assert.Equal(destination, remoteSource);
        Assert.Equal(0xc3, Assert.Single(payload));
        Assert.Equal(0, RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch) - before);
    }

    [Fact]
    public async Task SamePeerWithADifferentIpv6ScopeIsNotAMismatch()
    {
        var destination = Endpoint.From(IPAddress.Parse("fe80::1%7"), 53);
        var flow = FlowKey.Create(Endpoint.From(IPAddress.Parse("fe80::2"), 53000), destination, TransportProtocol.Udp, FlowOriginKind.Host);
        var sink = new FakeResponseSink();
        var logger = CreateInfoLevelLogger();
        var transport = new FakeTransport(AddressFamily.InterNetworkV6, 40010);
        await using var session = CreateSession(flow, transport, sink, logger);
        session.Start(static _ => { });

        Assert.True(await session.SendSpanAsync(destination, [0xa1], CancellationToken.None));
        var before = RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch);

        transport.EnqueueResponse(new UdpTransportDatagram(IPAddress.Parse("fe80::1%9"), SourceDomain: null, 53, new byte[] { 0xc3 }));

        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (responseFlow, remoteSource, payload, _) = await sink.Responses.Reader.ReadAsync(readTimeout.Token);
        Assert.Equal(flow, responseFlow);
        Assert.Equal(9u, remoteSource.Address.ScopeId);
        Assert.Equal(0xc3, Assert.Single(payload));
        // The decoded scope (the receive interface's) rides through to the reinjector unchanged, yet the
        // flow's own destination keeps the captured scope — a difference the counter must not report.
        Assert.Equal(7u, flow.Remote.Address.ScopeId);
        Assert.Equal(0, RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch) - before);
        Assert.DoesNotContain(logger.Events, static recorded => string.Equals(recorded.Name, ForeignSourceEvent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnIpv6ReplyFromDifferentAddressBitsInTheSameScopeIsAMismatch()
    {
        var destination = Endpoint.From(IPAddress.Parse("fe80::1%7"), 53);
        var flow = FlowKey.Create(Endpoint.From(IPAddress.Parse("fe80::2"), 53000), destination, TransportProtocol.Udp, FlowOriginKind.Host);
        var sink = new FakeResponseSink();
        var transport = new FakeTransport(AddressFamily.InterNetworkV6, 40010);
        await using var session = CreateSession(flow, transport, sink);
        session.Start(static _ => { });

        Assert.True(await session.SendSpanAsync(destination, [0xa1], CancellationToken.None));
        var before = RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch);

        var otherPeer = Endpoint.From(IPAddress.Parse("fe80::3%7"), 53);
        Assert.NotEqual(destination.Address.Bits, otherPeer.Address.Bits);
        transport.EnqueueResponse(new UdpTransportDatagram(otherPeer.Address, SourceDomain: null, otherPeer.Port, new byte[] { 0xc3 }));

        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, remoteSource, _, _) = await sink.Responses.Reader.ReadAsync(readTimeout.Token);
        Assert.Equal(otherPeer, remoteSource);
        Assert.Equal(1, RuntimeCounters.Shared.Get(RuntimeCounters.UdpResponseSourceMismatch) - before);
    }

    private static UdpProxySession CreateSession(FlowKey flow, FakeTransport transport, IUdpResponseSink sink, ILogger? logger = null)
    {
        var loopback = flow.Local.AddressFamily == AddressFamilyKind.IPv4 ? IPAddress.Loopback : IPAddress.IPv6Loopback;
        return new(new UdpProxySessionContext(
            flow,
            1,
            new UdpAssociation(
                flow,
                new RelayAlias(FlowKey.Create(
                    Endpoint.From(loopback, 40000),
                    Endpoint.From(loopback, 50000),
                    TransportProtocol.Udp,
                    FlowOriginKind.Host)),
                1,
                DateTimeOffset.UnixEpoch),
            transport,
            sink,
            MacAddress.Invalid,
            TimeProvider.System,
            static (_, _) => { },
            logger ?? NullLogger.Instance,
            s_receiveWindowPool,
            1537,
            CancellationToken.None));
    }

    private static string? Field(RecordedEvent recorded, string key)
    {
        foreach (var field in recorded.Fields)
        {
            if (string.Equals(field.Key, key, StringComparison.Ordinal)) return Convert.ToString(field.Value, CultureInfo.InvariantCulture);
        }

        return null;
    }
}
