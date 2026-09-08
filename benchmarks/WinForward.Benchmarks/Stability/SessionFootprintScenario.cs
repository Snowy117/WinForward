using System.Diagnostics;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

internal static class SessionFootprintScenario
{
    private static readonly byte[] PopulatePayload = [1];
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(100);

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
        var coordinator = new UdpProxyCoordinator(factory, NoopUdpResponseSink.Instance, sessions);
        var server = new Socks5Server("benchmark", "127.0.0.1", 1080, null, null);
        using var process = Process.GetCurrentProcess();
        var workingSetBefore = process.WorkingSet64;
        var gen0Before = GC.CollectionCount(0);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        await PopulateAsync(coordinator, factory, server, sessions).ConfigureAwait(false);
        await factory.WaitUntilCreatedAsync(sessions).ConfigureAwait(false);
        await Task.Delay(SettleDelay).ConfigureAwait(false);
        var workingSetDeltaBytes = process.WorkingSet64 - workingSetBefore;
        var gen0Collections = GC.CollectionCount(0) - gen0Before;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        await coordinator.DisposeAsync().ConfigureAwait(false);
        context.WriteResult(
            "udp.sessionFootprint",
            new { sessions },
            new { workingSetDeltaBytes, gen0Collections, allocatedBytes });
    }

    private static async Task PopulateAsync(UdpProxyCoordinator coordinator, CountingTransportFactory factory, Socks5Server server, int sessions)
    {
        var flowKeys = new FlowKey[sessions];
        for (var index = 0; index < flowKeys.Length; index++) flowKeys[index] = BenchmarkShared.CreateFlowKey(index);
        while (factory.Created < sessions)
        {
            foreach (var flowKey in flowKeys)
            {
                // A false return is the setup-failure cooldown; the next round re-offers the
                // flow and WaitUntilCreatedAsync bounds the total populate time.
                _ = await coordinator.TrySendAsync(flowKey, server, PopulatePayload, CancellationToken.None).ConfigureAwait(false);
            }

            await factory.WaitUntilProgressAsync().ConfigureAwait(false);
        }
    }

    private sealed class CountingTransportFactory(IUdpProxyTransportFactory inner) : IUdpProxyTransportFactory
    {
        private int _created;

        public int Created => Volatile.Read(ref _created);

        public async ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
        {
            var transport = await inner.CreateAsync(server, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _created);
            return transport;
        }

        public async Task WaitUntilCreatedAsync(int expected)
        {
            using var timeout = new CancellationTokenSource(SetupTimeout);
            while (Volatile.Read(ref _created) < expected)
            {
                try
                {
                    await Task.Delay(10, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"Only {Volatile.Read(ref _created)} of {expected} UDP sessions were created within the setup timeout.");
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
