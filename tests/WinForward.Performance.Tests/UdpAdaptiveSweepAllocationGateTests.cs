using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.UdpProxy;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;

namespace WinForward.Performance.Tests;

/// <summary>
/// The two-class idle sweep's allocation contract, in its own file because
/// <c>SweepAllocationGateTests.cs</c> is already over the 400-effective-line cap.
/// <para>
/// The gated tick is the <em>no-op</em> tick over a populated world: every session is past the
/// short-class pre-filter cutoff (so the candidate scan, the per-candidate transport-class read and
/// the per-class cutoff comparison all run) and none is past the long class's cutoff, so nothing
/// retires.
/// </para>
/// <para>
/// Window contract: bounded probe batches that must each read an exactly zero
/// per-thread delta on an unchanged thread before the measured window opens; the driven call must
/// complete synchronously; the managed thread id is captured before the window and asserted unchanged
/// after it; the assertion is the exact <c>Assert.Equal(0, allocated)</c>; and a thread-independent
/// population backstop proves the window was not vacuous.
/// </para>
/// </summary>
public sealed class UdpAdaptiveSweepAllocationGateTests
{
    private const int Sessions = 16;
    private const int MaximumFrameSize = 1_514;

    private static readonly TimeSpan s_long = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_short = UdpProxyCoordinator.OneShotIdleTimeout;
    private static readonly TimeSpan s_idle = TimeSpan.FromSeconds(10);
    private static readonly byte[] s_payload = [1];
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);

    [Fact]
    public async Task UdpProxyCoordinatorAdaptiveSweepAllocatesNoManagedBytes()
    {
        var factory = new FakeTransportFactory();
        using var setupQueuePool = new NativeBufferPool(MaximumFrameSize);
        using var receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(MaximumFrameSize));
        using var setupExecutor = new SetupExecutor();
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var clock = new ActivityBucketClock(time);
        await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(
            factory,
            new FakeResponseSink(),
            new UdpProxyOptions { Capacity = Sessions, TimeProvider = time, ActivityClock = clock },
            setupQueuePool,
            receiveWindowPool,
            setupExecutor);

        var start = time.GetUtcNow();
        for (var index = 0; index < Sessions; index++)
        {
            Assert.True(await coordinator.TrySendSpanAsync(MakeUdpFlow(checked((ushort)(53 + index))), ProxyTarget.FromServer(s_server), s_payload, default, CancellationToken.None));
        }

        // Population proof: the transports exist and every queued datagram was flushed (the flush is
        // what stamps each session's activity), so the sweep below scans a live world.
        await WaitForAsync(() => factory.Transports.Count == Sessions && factory.Transports.All(static transport => SentCount(transport) >= 1));
        Assert.Equal(Sessions, coordinator.SessionCount);

        // Between the two cutoffs: past the short class's pre-filter cutoff, inside the long class's.
        // The fake transports carry no exchange evidence, so every session is classified sustained.
        time.Advance(s_idle);
        clock.Tick();
        var now = time.GetUtcNow();
        Assert.True(ActivityBucket.FromUtc(now - s_short) > ActivityBucket.FromUtc(start), "the gated world must be past the short-class pre-filter cutoff");
        Assert.True(ActivityBucket.FromUtc(now - s_long) < ActivityBucket.FromUtc(start), "the gated world must be inside the long-class cutoff");

        const int maximumProbeTicks = 8;
        var stabilized = false;
        for (var tick = 0; tick < maximumProbeTicks && !stabilized; tick++)
        {
            var probeThreadId = Environment.CurrentManagedThreadId;
            var probeBefore = GC.GetAllocatedBytesForCurrentThread();
            var probe = coordinator.RemoveExpiredAsync(now, s_long, s_short);
            Assert.True(probe.IsCompletedSuccessfully, "the no-op sweep must complete synchronously");
            var probeRemoved = await probe;
            stabilized = Environment.CurrentManagedThreadId == probeThreadId && GC.GetAllocatedBytesForCurrentThread() == probeBefore;
            Assert.Equal(0, probeRemoved);
        }

        Assert.True(stabilized, "the udp adaptive sweep never became allocation-stable");

        var measuredThreadId = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var pending = coordinator.RemoveExpiredAsync(now, s_long, s_short);
        Assert.True(pending.IsCompletedSuccessfully, "the no-op sweep must complete synchronously");
        var removed = await pending;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
        Assert.Equal(0, removed);
        Assert.Equal(0, allocated);
        // Thread-independent backstop: every populated session survived the tick.
        Assert.Equal(Sessions, coordinator.SessionCount);
        Assert.Equal(Sessions, factory.Transports.Count);
    }

    private static int SentCount(FakeTransport transport)
    {
        lock (transport.Sent) return transport.Sent.Count;
    }

    private static FlowKey MakeUdpFlow(ushort remotePort) =>
        FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), 53_000),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), remotePort),
            TransportProtocol.Udp,
            FlowOriginKind.Host);
}
