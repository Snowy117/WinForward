using System.Diagnostics;
using System.Globalization;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The per-session footprint instrument: the documented <c>udp.sessionFootprint</c> row
/// (<c>workingSetDeltaBytes</c>, <c>gen0Collections</c>, <c>allocatedBytes</c>) at 1/100/1000
/// sessions, and <c>udp.sessionFootprint.cycle</c>, the receive-window pool's overflow growth
/// across a second population cycle.
/// <para>
/// A pool cycle is the only reading that discriminates the pool's sizing: the overflow counter
/// counts every rent that missed the free list, so a first fill of N live sessions reads N at
/// <em>any</em> capacity, while a second fill grows by the population the pool cannot hold. The
/// pool is built the way composition builds it, so the row describes the shipped shape.
/// </para>
/// </summary>
internal static class SessionFootprintScenario
{
    private static readonly byte[] s_populatePayload = [1];
    private static readonly TimeSpan s_setupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_settleDelay = TimeSpan.FromMilliseconds(100);

    public static async Task RunAsync(StabilityContext context, SoakOptions _)
    {
        foreach (var sessions in new[] { 1, 100, 1000 })
        {
            await RunOneAsync(context, sessions).ConfigureAwait(false);
        }
    }

    private static async Task RunOneAsync(StabilityContext context, int sessions)
    {
        var factory = new CountingTransportFactory(new BenchmarkUdpTransportFactory());
        const int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame;
        using var setupQueuePool = new NativeBufferPool(maximumFrameSize);
        using var receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize), UdpProxyCoordinator.ReceiveWindowPoolCapacity(sessions));
        using var setupExecutor = new SetupExecutor();
        var server = new Socks5Server("benchmark", "127.0.0.1", 1080, Username: null, Password: null);
        using var process = Process.GetCurrentProcess();

        // Cycle 1: the documented row, measured with its whole population live (disposal is the
        // first release, so the measurement must happen before it) and with the pool's cumulative
        // overflow counter read at that moment.
        var workingSetBefore = process.WorkingSet64;
        var gen0Before = GC.CollectionCount(0);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var coordinator = CreateCoordinator(factory, setupQueuePool, receiveWindowPool, setupExecutor, sessions);
        await PopulateAsync(coordinator, factory, server, sessions, sessions).ConfigureAwait(false);
        await Task.Delay(s_settleDelay).ConfigureAwait(false);
        var workingSetDeltaBytes = process.WorkingSet64 - workingSetBefore;
        var gen0Collections = GC.CollectionCount(0) - gen0Before;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var overflowFirstWave = receiveWindowPool.Stats.OverflowAllocations;
        await coordinator.DisposeAsync().ConfigureAwait(false);
        context.WriteResult(
            "udp.sessionFootprint",
            new { sessions },
            new { workingSetDeltaBytes, gen0Collections, allocatedBytes });

        // Cycle 2: the same population again over the same pool. The first cycle must have returned
        // every lease first, otherwise the reading would describe a leak rather than the pool's reuse.
        var released = receiveWindowPool.Stats;
        if (released.Outstanding != 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The first population cycle left {released.Outstanding} receive-window leases outstanding, so the second cycle's overflow reading would not describe the pool."));
        }

        var secondWave = CreateCoordinator(factory, setupQueuePool, receiveWindowPool, setupExecutor, sessions);
        await PopulateAsync(secondWave, factory, server, sessions, sessions * 2).ConfigureAwait(false);
        await Task.Delay(s_settleDelay).ConfigureAwait(false);
        var overflowSecondWave = receiveWindowPool.Stats.OverflowAllocations;
        await secondWave.DisposeAsync().ConfigureAwait(false);
        context.WriteResult(
            "udp.sessionFootprint.cycle",
            new { sessions },
            new { overflowFirstWave, overflowSecondWave });
    }

    private static UdpProxyCoordinator CreateCoordinator(
        IUdpProxyTransportFactory factory,
        NativeBufferPool setupQueuePool,
        NativeBufferPool receiveWindowPool,
        SetupExecutor setupExecutor,
        int sessions) =>
        new(factory, NoopUdpResponseSink.Instance, setupQueuePool, receiveWindowPool, setupExecutor, new UdpProxyOptions { Capacity = sessions });

    private static async Task PopulateAsync(UdpProxyCoordinator coordinator, CountingTransportFactory factory, Socks5Server server, int sessions, int targetCreated)
    {
        var flowKeys = new FlowKey[sessions];
        for (var index = 0; index < flowKeys.Length; index++) flowKeys[index] = BenchmarkShared.CreateFlowKey(index);
        while (factory.Created < targetCreated)
        {
            foreach (var flowKey in flowKeys)
            {
                // A false return is the setup-failure cooldown; the next round re-offers the
                // flow and WaitUntilCreatedAsync bounds the total populate time.
                _ = await coordinator.TrySendSpanAsync(flowKey, ProxyTarget.FromServer(server), s_populatePayload, default, CancellationToken.None).ConfigureAwait(false);
            }

            await factory.WaitUntilProgressAsync().ConfigureAwait(false);
        }

        await factory.WaitUntilCreatedAsync(targetCreated).ConfigureAwait(false);
    }

    private sealed class CountingTransportFactory(IUdpProxyTransportFactory inner) : IUdpProxyTransportFactory
    {
        private int _created;

        public int Created => Volatile.Read(ref _created);

        public async ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
        {
            var transport = await inner.CreateAsync(target, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _created);
            return transport;
        }

        public async Task WaitUntilCreatedAsync(int expected)
        {
            using var timeout = new CancellationTokenSource(s_setupTimeout);
            while (Volatile.Read(ref _created) < expected)
            {
                try
                {
                    await Task.Delay(10, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Only {Volatile.Read(ref _created)} of {expected} UDP sessions were created within the setup timeout."));
                }
            }
        }

        public async Task WaitUntilProgressAsync()
        {
            var before = Volatile.Read(ref _created);
            using var round = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                while (Volatile.Read(ref _created) == before)
                {
                    await Task.Delay(10, round.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // No progress in this round; the populate loop re-offers the remaining flows
                // after the setup-failure cooldown elapses.
            }
        }
    }
}
