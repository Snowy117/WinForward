using BenchmarkDotNet.Attributes;
using WinForward.NdisApi;

namespace WinForward.Benchmarks.Perf;

[MemoryDiagnoser]
public class NdisBufferBenchmarks
{
    private static long s_sink;

    [Params(64, 512, 1514)]
    public int FrameBytes { get; set; }

    private byte[] _frame = null!;
    private NdisPacketBuffer _buffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _frame = BenchmarkShared.CreateIpv4UdpFrame(FrameBytes);
        _buffer = new NdisPacketBuffer();
    }

    [GlobalCleanup]
    public void Cleanup() => _buffer.Dispose();

    [Benchmark]
    public long AllocateSetDispose()
    {
        long value = 0;
        using var buffer = new NdisPacketBuffer();
        buffer.SetFrame(_frame, NdisApiAbi.PacketFlagOnSend, (nint)1);
        value += buffer.Length;
        Volatile.Write(ref s_sink, value);
        return value;
    }

    [Benchmark]
    public long ReuseSet()
    {
        long value = 0;
        _buffer.SetFrame(_frame, NdisApiAbi.PacketFlagOnSend, (nint)1);
        value += _buffer.Length;
        Volatile.Write(ref s_sink, value);
        return value;
    }
}
