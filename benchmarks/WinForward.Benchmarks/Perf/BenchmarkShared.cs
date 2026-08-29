using System.Buffers.Binary;
using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Perf;

internal static class BenchmarkShared
{
    public static byte[] CreateIpv4UdpFrame(int frameSize)
    {
        if (frameSize < 64 || frameSize > UdpFrameBuilder.MaximumEthernetFrame) throw new ArgumentOutOfRangeException(nameof(frameSize));
        var frame = new byte[frameSize];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12, 2), 0x0800);
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), checked((ushort)(frameSize - 14)));
        frame[22] = 64;
        frame[23] = 17;
        IPAddress.Parse("192.0.2.10").TryWriteBytes(frame.AsSpan(26, 4), out _);
        IPAddress.Parse("192.0.2.53").TryWriteBytes(frame.AsSpan(30, 4), out _);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), 53_000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(38, 2), checked((ushort)(frameSize - 34)));
        for (var index = 42; index < frame.Length; index++) frame[index] = unchecked((byte)index);
        return frame;
    }

    public static byte[] CreateIpv4TcpFrame(int frameSize)
    {
        if (frameSize < 64 || frameSize > UdpFrameBuilder.MaximumEthernetFrame) throw new ArgumentOutOfRangeException(nameof(frameSize));
        var frame = new byte[frameSize];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12, 2), 0x0800);
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), checked((ushort)(frameSize - 14)));
        frame[22] = 64;
        frame[23] = 6;
        IPAddress.Parse("192.0.2.10").TryWriteBytes(frame.AsSpan(26, 4), out _);
        IPAddress.Parse("192.0.2.80").TryWriteBytes(frame.AsSpan(30, 4), out _);
        // The source port is rewritten per packet by the capture-pump reader to rotate flow keys.
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), 53_000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36, 2), 443);
        frame[46] = 0x50;
        for (var index = 54; index < frame.Length; index++) frame[index] = unchecked((byte)index);
        return frame;
    }

    /// <summary>
    /// Builds a TCP frame variant for the redirect-rewrite benchmarks: a bare SYN carries no
    /// payload (IP total length covers only the headers; trailing bytes are Ethernet padding),
    /// while the mid-flow data variant fills the frame to the end. TCP flags distinguish the
    /// shapes (SYN vs ACK); header checksums stay zero because neither the classifier nor the
    /// endpoint rewriter validates the input checksum — the rewriter recomputes both.
    /// </summary>
    public static byte[] CreateIpv4TcpFrame(int frameSize, bool bareSyn)
    {
        var frame = CreateIpv4TcpFrame(frameSize);
        const int IpTotalLengthOffset = 16;
        const int TcpFlagsOffset = 47;
        if (bareSyn)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(IpTotalLengthOffset, 2), 40);
            frame[TcpFlagsOffset] = 0x02;
        }
        else
        {
            frame[TcpFlagsOffset] = 0x10;
        }

        return frame;
    }

    public static FlowKey CreateFlowKey(int index)
    {
        var first = index / 65_536;
        var second = index % 65_536;
        var local = Endpoint.From(IPAddress.Parse($"10.{first % 256}.{second / 256}.{second % 256}"), checked((ushort)(1_024 + index % 50_000)));
        var remote = Endpoint.From(IPAddress.Parse($"172.{16 + first % 16}.{second / 256}.{second % 256}"), checked((ushort)(1 + index % 65_535)));
        return FlowKey.Create(local, remote, TransportProtocol.Udp, FlowOriginKind.Host, new AdapterContext($"adapter-{index % 4}", null, index % 4));
    }

    public static FlowContext CreateContext(FlowKey key) => new(key, null, null, key.OriginAdapterId, null, key.Remote.Port);
}

internal sealed class NeverOwnedGuard : ISelfTrafficGuard
{
    public bool IsOwned(FlowContext context) => false;
}

internal sealed class CountingExecutor : IPacketActionExecutor
{
    public long PassCount { get; private set; }
    public long ProxyCount { get; private set; }
    public ValueTask PassAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        PassCount++;
        return ValueTask.CompletedTask;
    }
    public ValueTask BlockAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken)
    {
        ProxyCount++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ThresholdOnlyLogger(RuntimeLogLevel threshold) : IRuntimeLogger
{
    public bool IsEnabled(RuntimeLogLevel level) => level <= threshold;
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
}

internal sealed class NoopAsyncDisposable : IAsyncDisposable
{
    public static readonly NoopAsyncDisposable Instance = new();
    private NoopAsyncDisposable() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class BenchmarkUdpTransportFactory : IUdpProxyTransportFactory
{
    private int _nextPort = 10_000;
    private long _sends;

    /// <summary>Total datagrams handed to fake transports' <c>SendAsync</c>; the setup-queue flush increments it once per drained datagram.</summary>
    public long Sends => Interlocked.Read(ref _sends);

    internal void NoteSend() => Interlocked.Increment(ref _sends);

    public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IUdpProxyTransport>(new BenchmarkUdpTransport(Interlocked.Increment(ref _nextPort), this));
}

internal sealed class BenchmarkUdpTransport(int localPort, BenchmarkUdpTransportFactory owner) : IUdpProxyTransport
{
    public IPEndPoint RelayEndpoint { get; } = new(IPAddress.Loopback, 50_000);
    public IPEndPoint LocalEndpoint { get; } = new(IPAddress.Loopback, localPort);

    public ValueTask SendAsync(IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        owner.NoteSend();
        return ValueTask.CompletedTask;
    }

    public async ValueTask<Socks5UdpReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("The benchmark receive should end through cancellation.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class NoopUdpResponseSink : IUdpResponseSink
{
    public static readonly NoopUdpResponseSink Instance = new();
    private NoopUdpResponseSink() { }
    public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, byte[]? clientMac, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
