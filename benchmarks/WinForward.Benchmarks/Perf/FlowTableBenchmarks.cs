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
            BenchmarkShared.SlotOf("missing", 99),
            99);
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

        var baseKey = BenchmarkShared.CreateFlowKey(Cardinality / 2);
        _hit = FlowKey.Create(
            baseKey.Local,
            baseKey.Remote,
            baseKey.Protocol,
            FlowOriginKind.Forwarded,
            BenchmarkShared.SlotOf("other-adapter", 42),
            42);
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

/// <summary>
/// The flow table at its production cardinality (the default <c>tcpFlowCapacity</c>, 4,096) in the two
/// orientations a proxied connection actually resolves.
/// <para>
/// A change to the key's hash or the orientation index can be read as a delta here. The claim path is
/// deliberately not a row: BenchmarkDotNet invokes one method many times per iteration and this table
/// has no per-invocation reset, so a "new key" claim would silently degrade into a resolve partway
/// through the iteration.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class FlowTableProductionShapeBenchmarks
{
    private static long s_sink;

    private const int Cardinality = 4_096;

    private FlowTable _table = null!;
    private FlowKey _stored;
    private FlowKey _reverse;

    [GlobalSetup]
    public void Setup()
    {
        _table = new FlowTable(capacity: Cardinality + 2);
        for (var index = 0; index < Cardinality; index++)
        {
            if (!_table.TryClaimResolved(BenchmarkShared.CreateFlowKey(index), static () => FlowDecision.Fallback(FlowAction.Pass), out _))
            {
                throw new InvalidOperationException("Unable to populate the flow-table production-shape benchmark.");
            }
        }

        _stored = BenchmarkShared.CreateFlowKey(Cardinality / 2);

        _reverse = _stored.Reverse();

        // A row that silently measured the miss path would report a plausible number for the wrong
        // question, so both orientations are proven to resolve before either is timed.
        if (!_table.TryResolve(_stored, out _) || !_table.TryResolve(_reverse, out _))
        {
            throw new InvalidOperationException("The production-shape rows must resolve both the stored and the reverse orientation.");
        }
    }

    [Benchmark]
    public long ResolveSameOrientationHit()
    {
        long value = 0;
        value += _table.TryResolve(_stored, out var state) ? state!.Generation : 0;
        Volatile.Write(ref s_sink, value);
        return value;
    }

    [Benchmark]
    public long ResolveReverseAliasHit()
    {
        long value = 0;
        value += _table.TryResolve(_reverse, out var state) ? state!.Generation : 0;
        Volatile.Write(ref s_sink, value);
        return value;
    }

    /// <summary>
    /// The dispatcher's warm entry: the direct-mapped cache probe with its seqlock snapshot and
    /// transport-tuple corroboration, no gate and no clock read.
    /// </summary>
    [Benchmark]
    public long ResolveWarmHit()
    {
        long value = 0;
        value += _table.TryResolveWarm(_stored, out var view) ? view.Generation : 0;
        Volatile.Write(ref s_sink, value);
        return value;
    }
}
