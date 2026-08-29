using BenchmarkDotNet.Attributes;
using WinForward.Configuration;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Perf;

[MemoryDiagnoser]
public class UdpSessionBenchmarks
{
    [Params(1, 100, 1000)]
    public int Sessions { get; set; }

    [Benchmark]
    public async Task PopulateSessionsAsync()
    {
        await using var coordinator = new UdpProxyCoordinator(new BenchmarkUdpTransportFactory(), NoopUdpResponseSink.Instance, Sessions);
        var server = new Socks5Server("benchmark", "127.0.0.1", 1080, null, null);
        for (var index = 0; index < Sessions; index++)
        {
            if (!await coordinator.TrySendAsync(BenchmarkShared.CreateFlowKey(index), server, new byte[] { 1 }, CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Unable to populate the UDP session benchmark.");
            }
        }
    }
}
