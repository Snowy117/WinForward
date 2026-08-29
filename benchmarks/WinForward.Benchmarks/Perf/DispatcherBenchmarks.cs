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

    private FlowDispatcher _proxyDispatcher = null!;
    private CountingExecutor _proxyExecutor = null!;
    private long _proxySequence;

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

        var proxyConfiguration = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase)
            {
                ["benchmark"] = new Socks5Server("benchmark", "127.0.0.1", 1080, null, null),
            },
            new PolicySnapshot(
                [new PolicyRule(new RuleMatcher(), new FlowDecision(FlowAction.Proxy, 0, "benchmark"))],
                FlowAction.Pass));
        _proxyExecutor = new CountingExecutor();
        _proxyDispatcher = new FlowDispatcher(proxyConfiguration, new NeverOwnedGuard(), _proxyExecutor, logger: logger);
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

    /// <summary>
    /// The proxy-branch counterpart of <see cref="WarmPassDisabledTraceAsync"/>: a resolved flow
    /// whose policy decision is Proxy with a configured server. Both actions ride the non-async
    /// warm entry (the proxy branch resolves the server inline and returns the executor's
    /// ValueTask directly), so this number minus the pass number isolates the proxy-specific
    /// warm work: the server dictionary lookup plus the reverse-of-stored guard.
    /// </summary>
    [Benchmark]
    public async Task WarmProxyDisabledTraceAsync()
    {
        var lease = new PacketLease(new byte[64]);
        var packet = new CapturedFlowPacket(lease, BenchmarkShared.CreateContext(_key), PacketSequence: ++_proxySequence);
        await _proxyDispatcher.DispatchAsync(packet, CancellationToken.None).ConfigureAwait(false);
        var bytes = lease.Frame.Length;
        Volatile.Write(ref s_sink, bytes + _proxyExecutor.ProxyCount);
    }
}
