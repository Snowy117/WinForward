using BenchmarkDotNet.Attributes;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;

namespace WinForward.Benchmarks.Perf;

[MemoryDiagnoser]
public class DispatcherBenchmarks
{
    private static long s_sink;

    private FlowDispatcher _dispatcher = null!;
    private CountingExecutor _executor = null!;
    private FlowKey _key;
    private long _sequence;

    [GlobalSetup]
    public void Setup()
    {
        var passConfiguration = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass));
        _executor = new CountingExecutor();
        var logger = new ThresholdOnlyLogger(RuntimeLogLevel.Info);
        _dispatcher = new FlowDispatcher(passConfiguration, new NeverOwnedGuard(), _executor, logger: logger);
        _key = BenchmarkShared.CreateFlowKey(0);
    }

    [Benchmark]
    public async Task WarmPassDisabledTraceAsync()
    {
        var lease = new PacketLease(new byte[64]);
        var packet = new CapturedFlowPacket(lease, BenchmarkShared.CreateContext(_key), PacketSequence: ++_sequence);
        await _dispatcher.DispatchAsync(packet, CancellationToken.None).ConfigureAwait(false);
        var bytes = lease.Frame.Length;
        Volatile.Write(ref s_sink, bytes + _executor.PassCount);
    }
}
