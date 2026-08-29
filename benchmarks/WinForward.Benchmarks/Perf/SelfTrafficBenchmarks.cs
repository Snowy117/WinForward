using System.Net;
using BenchmarkDotNet.Attributes;
using WinForward.Core;
using WinForward.Runtime;

namespace WinForward.Benchmarks.Perf;

[MemoryDiagnoser]
public class SelfTrafficBenchmarks
{
    private static long s_sink;

    [Params(0, 1000, 16384, 65535)]
    public int Cardinality { get; set; }

    private SelfTrafficRegistry _registry = null!;
    private List<IDisposable> _tokens = null!;
    private FlowContext _missing;

    [GlobalSetup]
    public void Setup()
    {
        _registry = new SelfTrafficRegistry();
        _tokens = new List<IDisposable>(Cardinality);
        for (var index = 0; index < Cardinality; index++)
        {
            var remote = Endpoint.From(IPAddress.Parse($"198.19.{index / 256 % 256}.{index % 256}"), checked((ushort)(10_000 + index % 50_000)));
            _tokens.Add(_registry.Register(new SelfTrafficRegistry.SelfTrafficKey(
                TransportProtocol.Udp,
                Endpoint.From(IPAddress.Any, checked((ushort)(1_024 + index % 50_000))),
                remote)));
        }

        _missing = new FlowContext(
            FlowKey.Create(
                Endpoint.From(IPAddress.Parse("192.0.2.10"), 60_000),
                Endpoint.From(IPAddress.Parse("203.0.113.10"), 53),
                TransportProtocol.Udp,
                FlowOriginKind.Host),
            null, null, null, null, 53);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var token in _tokens) token.Dispose();
    }

    [Benchmark]
    public long WildcardMiss()
    {
        long value = 0;
        value += _registry.IsOwned(_missing) ? 1 : 0;
        Volatile.Write(ref s_sink, value);
        return value;
    }
}
