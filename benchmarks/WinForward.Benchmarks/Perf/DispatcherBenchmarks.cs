using BenchmarkDotNet.Attributes;
using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;

namespace WinForward.Benchmarks.Perf;

[MemoryDiagnoser]
public class DispatcherBenchmarks
{
    private static long s_sink;

    /// <summary>The listener port claimed in the prefilter table for the diverted control (X1).</summary>
    private const ushort CandidatePort = 40_000;

    private FlowDispatcher _dispatcher = null!;
    private CountingExecutor _executor = null!;
    private FlowKey _key;
    private long _sequence;

    private FlowDispatcher _proxyDispatcher = null!;
    private CountingExecutor _proxyExecutor = null!;
    private long _proxySequence;

    private FlowDispatcher _productionDispatcher = null!;
    private CountingExecutor _productionExecutor = null!;
    private FlowKey _productionUdpKey;
    private FlowKey _productionTcpKey;
    private long _productionSequence;

    private FlowDispatcher _productionProxyDispatcher = null!;
    private CountingExecutor _productionProxyExecutor = null!;
    private long _productionProxySequence;

    private FlowDispatcher _candidateDispatcher = null!;
    private CountingExecutor _candidateExecutor = null!;
    private FlowKey _candidateKey;
    private long _candidateSequence;

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

        // The production compositions wire a reverse handler (X1); the handler runs the real
        // predicate path (protocol gate + a real TcpRedirectTable port array) and answers
        // NotRelevant from its handling side, which warm packets never reach.
        var reverseTable = new TcpRedirectTable();
        var reverseHandler = new PrefilterReverseHandler(reverseTable);
        _productionUdpKey = BenchmarkShared.CreateFlowKey(0);
        _productionTcpKey = BenchmarkShared.CreateTcpFlowKey(53_000);
        _productionExecutor = new CountingExecutor();
        _productionDispatcher = new FlowDispatcher(passConfiguration, new NeverOwnedGuard(), _productionExecutor, reverseHandler: reverseHandler, logger: logger);

        _productionProxyExecutor = new CountingExecutor();
        _productionProxyDispatcher = new FlowDispatcher(proxyConfiguration, new NeverOwnedGuard(), _productionProxyExecutor, reverseHandler: reverseHandler, logger: logger);

        // The diverted control: a claimed listener port makes the candidate key's source port a
        // prefilter hit, so every dispatch pays the slow path — the shape 100% of production
        // packets ran before the X1 fix.
        var claimKey = BenchmarkShared.CreateTcpFlowKey(CandidatePort);
        AssertClaim(reverseTable, claimKey, CandidatePort);
        _candidateKey = claimKey;
        _candidateExecutor = new CountingExecutor();
        _candidateDispatcher = new FlowDispatcher(passConfiguration, new NeverOwnedGuard(), _candidateExecutor, reverseHandler: reverseHandler, logger: logger);
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

    /// <summary>
    /// The production-composition counterpart of <see cref="WarmPassDisabledTraceAsync"/> (X1):
    /// identical shape plus a wired reverse handler, which is how <c>Program</c> always builds the
    /// dispatcher. The handler's prefilter declines this UDP key, so the packet must ride the warm
    /// entry — this number matching the handler-less 160 B baseline proves the warm entry is live
    /// in the production shape.
    /// </summary>
    [Benchmark]
    public async Task WarmPassProductionAsync()
    {
        var lease = new PacketLease(new byte[64]);
        var packet = new CapturedFlowPacket(lease, BenchmarkShared.CreateContext(_productionUdpKey), PacketSequence: ++_productionSequence);
        await _productionDispatcher.DispatchAsync(packet, CancellationToken.None).ConfigureAwait(false);
        var bytes = lease.Frame.Length;
        Volatile.Write(ref s_sink, bytes + _productionExecutor.PassCount);
    }

    /// <summary>
    /// The production main path under a wired reverse handler (X1): a resolved TCP proxy decision
    /// whose source port is no live listener port, so the prefilter declines and the packet rides
    /// the warm entry — the strictest warm shape, paying the full predicate (protocol gate + port
    /// array probe) plus the inline server resolution.
    /// </summary>
    [Benchmark]
    public async Task WarmProxyProductionAsync()
    {
        var lease = new PacketLease(new byte[64]);
        var packet = new CapturedFlowPacket(lease, BenchmarkShared.CreateContext(_productionTcpKey), PacketSequence: ++_productionProxySequence);
        await _productionProxyDispatcher.DispatchAsync(packet, CancellationToken.None).ConfigureAwait(false);
        var bytes = lease.Frame.Length;
        Volatile.Write(ref s_sink, bytes + _productionProxyExecutor.ProxyCount);
    }

    /// <summary>
    /// The diverted control (X1): a TCP key whose source port is a claimed listener port, so the
    /// prefilter diverts every dispatch into <see cref="FlowDispatcher.DispatchSlowAsync"/>. This
    /// is the per-packet cost 100% of production traffic paid before the warm-entry revival.
    /// </summary>
    [Benchmark]
    public async Task ReverseCandidateSlowPathAsync()
    {
        var lease = new PacketLease(new byte[64]);
        var packet = new CapturedFlowPacket(lease, BenchmarkShared.CreateContext(_candidateKey), PacketSequence: ++_candidateSequence);
        await _candidateDispatcher.DispatchAsync(packet, CancellationToken.None).ConfigureAwait(false);
        var bytes = lease.Frame.Length;
        Volatile.Write(ref s_sink, bytes + _candidateExecutor.PassCount);
    }

    private static void AssertClaim(TcpRedirectTable table, FlowKey originalKey, ushort listenerPort)
    {
        var translated = Endpoint.From(IPAddress.Loopback, listenerPort);
        if (!table.TryClaim(originalKey, originalKey.Remote, originalKey.OriginAdapterId is { } adapterId ? new AdapterContext(adapterId, null, 0) : new AdapterContext("adapter-0", null, 0), 0x1234, translated, null, DateTimeOffset.UtcNow, out _))
            throw new InvalidOperationException("The candidate-port claim must succeed for the diverted control.");
    }
}

/// <summary>
/// The production reverse-handler stand-in for dispatcher benchmarks (X1): its predicate runs the
/// real path (protocol gate + the live <see cref="TcpRedirectTable"/> port array), and its handling
/// side answers NotRelevant — an answer warm packets never trigger, so it only shapes the slow path
/// of the diverted control.
/// </summary>
internal sealed class PrefilterReverseHandler(TcpRedirectTable table) : ITcpReverseHandler
{
    public bool WantsPacket(in CapturedFlowPacket packet)
    {
        var key = packet.Context.Key;
        return key.Protocol == TransportProtocol.Tcp && table.IsReverseCandidatePort(key.Local.Port);
    }

    public ValueTask<TcpRedirectOutcome> HandleReverseIfApplicableAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
        => ValueTask.FromResult(TcpRedirectOutcome.NotRelevant);
}
