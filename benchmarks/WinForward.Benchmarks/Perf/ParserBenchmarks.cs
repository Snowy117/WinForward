using System.Net;
using BenchmarkDotNet.Attributes;
using WinForward.Core;
using WinForward.Protocols;

namespace WinForward.Benchmarks.Perf;

[MemoryDiagnoser]
public class ParserBenchmarks
{
    private static long s_sink;

    [Params(64, 512, 1514)]
    public int FrameBytes { get; set; }

    private byte[] _frame = null!;
    private byte[] _socksFrame = null!;
    private byte[] _reusable = null!;
    private IPAddress _destination = null!;
    private int _payloadLength;

    [GlobalSetup]
    public void Setup()
    {
        _payloadLength = FrameBytes - 14 - 20 - 8;
        _frame = BenchmarkShared.CreateIpv4UdpFrame(FrameBytes);
        _destination = IPAddress.Parse("192.0.2.53");
        _socksFrame = Socks5UdpCodec.Encode(_destination, 53, _frame.AsSpan(_frame.Length - _payloadLength));
        _reusable = new byte[22 + 65535];
    }

    [Benchmark]
    public long Ipv4UdpTryParse()
    {
        long value = 0;
        if (!IPTcpUdpPacket.TryParse(_frame, out var packet)) throw new InvalidOperationException("Parser rejected the benchmark frame.");
        value += packet.SourcePort + packet.DestinationPort;
        Volatile.Write(ref s_sink, value);
        return value;
    }

    [Benchmark]
    public long Ipv4UdpPayload()
    {
        long value = 0;
        if (!IPUdpPacket.TryParse(_frame, out var packet)) throw new InvalidOperationException("UDP parser rejected the benchmark frame.");
        value += packet.Payload.Length;
        Volatile.Write(ref s_sink, value);
        return value;
    }

    [Benchmark]
    public long Socks5UdpDecode()
    {
        long value = 0;
        if (!Socks5UdpCodec.TryDecode(_socksFrame, out var datagram)) throw new InvalidOperationException("SOCKS5 UDP decoder rejected the benchmark frame.");
        value += datagram.Payload.Length;
        Volatile.Write(ref s_sink, value);
        return value;
    }

    [Benchmark]
    public long Socks5UdpEncode()
    {
        long value = 0;
        var payload = _frame.AsSpan(_frame.Length - _payloadLength);
        value += Socks5UdpCodec.Encode(_destination, 53, payload).Length;
        Volatile.Write(ref s_sink, value);
        return value;
    }

    [Benchmark]
    public long Socks5UdpEncodeSpan()
    {
        long value = 0;
        var payload = _frame.AsSpan(_frame.Length - _payloadLength);
        if (!Socks5UdpCodec.TryEncode(IPAddressValue.From(_destination), 53, payload, _reusable, out var written)) throw new InvalidOperationException("SOCKS5 UDP span encoder rejected the benchmark payload.");
        value += written;
        Volatile.Write(ref s_sink, value);
        return value;
    }
}
