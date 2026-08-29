using System.Net;
using BenchmarkDotNet.Attributes;
using WinForward.Core;

namespace WinForward.Benchmarks.Perf;

[MemoryDiagnoser]
public class FlowTableMissBenchmarks
{
    private static long s_sink;

    [Params(0, 1000, 16384, 65535)]
    public int Cardinality { get; set; }

    private FlowTable _table = null!;
    private FlowKey _missing;

    [GlobalSetup]
    public void Setup()
    {
        _table = new FlowTable(capacity: Math.Max(1, Cardinality + 2));
        for (var index = 0; index < Cardinality; index++)
        {
            var key = BenchmarkShared.CreateFlowKey(index);
            if (!_table.TryClaimResolved(key, static () => FlowDecision.Fallback(FlowAction.Pass), out _))
            {
                throw new InvalidOperationException("Unable to populate the flow-table benchmark.");
            }
        }

        _missing = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("198.18.255.254"), 65534),
            Endpoint.From(IPAddress.Parse("203.0.113.254"), 65535),
            TransportProtocol.Udp,
            FlowOriginKind.Forwarded,
            new AdapterContext("missing", "missing", 99));
    }

    [Benchmark]
    public long ResolveMissing()
    {
        long value = 0;
        value += _table.TryResolve(_missing, out _) ? 1 : 0;
        Volatile.Write(ref s_sink, value);
        return value;
    }
}

[MemoryDiagnoser]
public class FlowTableHitBenchmarks
{
    private static long s_sink;

    [Params(1000, 16384, 65535)]
    public int Cardinality { get; set; }

    private FlowTable _table = null!;
    private FlowKey _hit;

    [GlobalSetup]
    public void Setup()
    {
        _table = new FlowTable(capacity: Cardinality + 2);
        for (var index = 0; index < Cardinality; index++)
        {
            var key = BenchmarkShared.CreateFlowKey(index);
            if (!_table.TryClaimResolved(key, static () => FlowDecision.Fallback(FlowAction.Pass), out _))
            {
                throw new InvalidOperationException("Unable to populate the flow-table benchmark.");
            }
        }

        _hit = BenchmarkShared.CreateFlowKey(Cardinality / 2) with
        {
            Origin = FlowOriginKind.Forwarded,
            OriginAdapterId = "other-adapter",
            OriginAdapterGeneration = 42,
        };
    }

    [Benchmark]
    public long ResolveCrossAdapterHit()
    {
        long value = 0;
        value += _table.TryResolve(_hit, out var state) ? state!.Generation : 0;
        Volatile.Write(ref s_sink, value);
        return value;
    }
}
