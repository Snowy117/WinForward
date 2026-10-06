using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks;

internal static class BenchmarkShared
{
    public static byte[] CreateIpv4UdpFrame(int frameSize)
    {
        if (frameSize is < 64 or > UdpFrameBuilder.MaximumEthernetFrame) throw new ArgumentOutOfRangeException(nameof(frameSize));
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
        if (frameSize is < 64 or > UdpFrameBuilder.MaximumEthernetFrame) throw new ArgumentOutOfRangeException(nameof(frameSize));
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
    /// shapes (SYN vs ACK). Both variants carry valid IPv4 header and TCP checksums, matching
    /// captured traffic — required since the endpoint rewriter updates checksums incrementally
    /// from the incoming values (RFC 1624).
    /// </summary>
    public static byte[] CreateIpv4TcpFrame(int frameSize, bool bareSyn)
    {
        var frame = CreateIpv4TcpFrame(frameSize);
        const int ipTotalLengthOffset = 16;
        const int tcpFlagsOffset = 47;
        if (bareSyn)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipTotalLengthOffset, 2), 40);
            frame[tcpFlagsOffset] = 0x02;
        }
        else
        {
            frame[tcpFlagsOffset] = 0x10;
        }

        var headerLength = (frame[14] & 0x0f) * 4;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(ipTotalLengthOffset, 2));
        var tcpOffset = 14 + headerLength;
        var tcpLength = totalLength - headerLength;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(24, 2), PacketChecksums.InternetChecksum(frame.AsSpan(14, headerLength)));
        frame[tcpOffset + 16] = 0;
        frame[tcpOffset + 17] = 0;
        var sum = Sum16(frame.AsSpan(26, 4)) + Sum16(frame.AsSpan(30, 4)) + 6u + (uint)tcpLength + Sum16(frame.AsSpan(tcpOffset, tcpLength));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset + 16, 2), (ushort)~Fold16(sum));
        return frame;
    }

    /// <summary>
    /// The IPv6 counterpart of <see cref="CreateIpv4TcpFrame(int, bool)"/>: Ethernet + IPv6 (40 bytes,
    /// next header TCP) + TCP (20 bytes), same bare-SYN/mid-flow variants, with the TCP checksum
    /// computed over the IPv6 pseudo-header — which is what the endpoint rewriter's incremental update
    /// (RFC 1624) starts from, so a frame without it would measure a different path than captured
    /// traffic takes. The checksum is verified independently by <c>BenchmarkFrameBuilderTests</c>.
    /// </summary>
    public static byte[] CreateIpv6TcpFrame(int frameSize, bool bareSyn)
    {
        const int ethernetLength = 14;
        const int ipv6Length = 40;
        const int tcpOffset = ethernetLength + ipv6Length;
        if (frameSize is < tcpOffset + 20 or > UdpFrameBuilder.MaximumEthernetFrame) throw new ArgumentOutOfRangeException(nameof(frameSize));
        var frame = new byte[frameSize];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12, 2), 0x86dd);
        frame[ethernetLength] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18, 2), checked((ushort)(frameSize - ethernetLength - ipv6Length)));
        frame[20] = 6;
        frame[21] = 64;
        IPAddress.Parse("2001:db8::10").TryWriteBytes(frame.AsSpan(22, 16), out _);
        IPAddress.Parse("2001:db8::80").TryWriteBytes(frame.AsSpan(38, 16), out _);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset, 2), 53_000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset + 2, 2), 443);
        frame[tcpOffset + 12] = 0x50;
        for (var index = tcpOffset + 20; index < frame.Length; index++) frame[index] = unchecked((byte)index);

        if (bareSyn)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18, 2), 20);
            frame[tcpOffset + 13] = 0x02;
        }
        else
        {
            frame[tcpOffset + 13] = 0x10;
        }

        var tcpLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(18, 2));
        frame[tcpOffset + 16] = 0;
        frame[tcpOffset + 17] = 0;
        var sum = Sum16(frame.AsSpan(22, 16)) + Sum16(frame.AsSpan(38, 16)) + 6u + tcpLength + Sum16(frame.AsSpan(tcpOffset, tcpLength));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(tcpOffset + 16, 2), (ushort)~Fold16(sum));
        return frame;
    }

    /// <summary>
    /// The IPv6 counterpart of <see cref="CreateIpv4UdpFrame"/>: Ethernet + IPv6 (40 bytes, next header
    /// UDP) + UDP (8 bytes) + payload, same field layout conventions as <see cref="CreateIpv6TcpFrame"/>.
    /// The UDP checksum stays zero, as in the IPv4 UDP builder and the test project's IPv6 UDP builder:
    /// these are parse-only shapes, and neither <c>IPTcpUdpPacket</c> nor <c>IPUdpPacket</c> reads the
    /// checksum, so computing one would add setup work without changing the measured parse.
    /// </summary>
    public static byte[] CreateIpv6UdpFrame(int frameSize)
    {
        const int ethernetLength = 14;
        const int ipv6Length = 40;
        const int udpOffset = ethernetLength + ipv6Length;
        if (frameSize is < 64 or > UdpFrameBuilder.MaximumEthernetFrame) throw new ArgumentOutOfRangeException(nameof(frameSize));
        var frame = new byte[frameSize];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12, 2), 0x86dd);
        frame[ethernetLength] = 0x60;
        // The IPv6 payload length covers the UDP header plus the payload, and the UDP length repeats it.
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18, 2), checked((ushort)(frameSize - udpOffset)));
        frame[20] = 17;
        frame[21] = 64;
        IPAddress.Parse("2001:db8::10").TryWriteBytes(frame.AsSpan(22, 16), out _);
        IPAddress.Parse("2001:db8::53").TryWriteBytes(frame.AsSpan(38, 16), out _);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset, 2), 53_000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 2, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udpOffset + 4, 2), checked((ushort)(frameSize - udpOffset)));
        for (var index = udpOffset + 8; index < frame.Length; index++) frame[index] = unchecked((byte)index);
        return frame;
    }

    private static uint Sum16(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var index = 0;
        for (; index + 1 < data.Length; index += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
        if (index < data.Length) sum += (uint)data[index] << 8;
        return sum;
    }

    private static ushort Fold16(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)sum;
    }

    private static readonly AdapterSlotTable s_slots = new();

    /// <summary>The interned slot for a benchmark adapter stable ID (cold, setup-time only).</summary>
    public static ushort SlotOf(string stableId, long generation = 0)
    {
        s_slots.TryIntern(stableId, generation, stableId, out var slot);
        return slot;
    }

    public static FlowKey CreateFlowKey(int index)
    {
        var first = index / 65_536;
        var second = index % 65_536;
        var local = Endpoint.From(IPAddress.Parse(string.Create(CultureInfo.InvariantCulture, $"10.{first % 256}.{second / 256}.{second % 256}")), checked((ushort)(1_024 + (index % 50_000))));
        var remote = Endpoint.From(IPAddress.Parse(string.Create(CultureInfo.InvariantCulture, $"172.{16 + (first % 16)}.{second / 256}.{second % 256}")), checked((ushort)(1 + (index % 65_535))));
        return FlowKey.Create(local, remote, TransportProtocol.Udp, FlowOriginKind.Host, SlotOf(string.Create(CultureInfo.InvariantCulture, $"adapter-{index % 4}"), index % 4), index % 4);
    }

    /// <summary>A TCP flow key with a controllable local (source) port, for the reverse-prefilter
    /// benchmark shapes (X1): a candidate key's source port matches a claimed listener port.</summary>
    public static FlowKey CreateTcpFlowKey(ushort localPort)
    {
        var local = Endpoint.From(IPAddress.Parse("10.0.0.1"), localPort);
        var remote = Endpoint.From(IPAddress.Parse("172.16.0.1"), 443);
        return FlowKey.Create(local, remote, TransportProtocol.Tcp, FlowOriginKind.Host, SlotOf("adapter-0"), 0);
    }

    public static FlowContext CreateContext(FlowKey key) => new(key, AdapterForLogs(key), Process: null);

    /// <summary>The benchmark context's interned adapter identity, resolved from the key's slot (cold).</summary>
    private static AdapterMetadata? AdapterForLogs(FlowKey key) => s_slots.TryResolve(key.OriginAdapterSlot, out var metadata) ? metadata : null;
}

internal sealed class NeverOwnedGuard : ISelfTrafficGuard
{
    public bool IsOwned(FlowContext context) => false;

    public bool IsWildcardOwned(FlowContext context) => false;
}

internal sealed class CountingExecutor : IPacketActionExecutor
{
    public long PassCount { get; private set; }
    public long ProxyCount { get; private set; }
    public ValueTask PassAsync(CapturedFlowPacket packet)
    {
        PassCount++;
        return ValueTask.CompletedTask;
    }
    public ValueTask BlockAsync(CapturedFlowPacket packet) => ValueTask.CompletedTask;
    public ValueTask ProxyAsync(CapturedFlowPacket packet, ProxyTarget target, CancellationToken cancellationToken)
    {
        ProxyCount++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ThresholdOnlyLogger(LogLevel threshold) : ILogger
{
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= threshold;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
    }
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

    /// <summary>Total datagrams handed to fake transports' <c>SendSpanAsync</c>; the setup-queue flush increments it once per drained datagram.</summary>
    public long Sends => Interlocked.Read(ref _sends);

    internal void NoteSend() => Interlocked.Increment(ref _sends);

    public ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IUdpProxyTransport>(new BenchmarkUdpTransport(Interlocked.Increment(ref _nextPort), this));
}

internal sealed class BenchmarkUdpTransport(int localPort, BenchmarkUdpTransportFactory owner) : IUdpProxyTransport
{
    public IPEndPoint PeerEndpoint { get; } = new(IPAddress.Loopback, 50_000);
    public IPEndPoint LocalEndpoint { get; } = new(IPAddress.Loopback, localPort);

    public ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
    {
        owner.NoteSend();
        return ValueTask.CompletedTask;
    }

    public async ValueTask<UdpTransportReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("The benchmark receive should end through cancellation.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// A fake relay transport that also carries the exchange evidence, so a retention scenario can place
/// a flow in either retention class without driving real datagrams through it. The counters are
/// settable by the scenario and read back through <see cref="IUdpExchangeCounters"/> as part of its
/// population proof.
/// </summary>
internal sealed class BenchmarkExchangeTransport(int localPort) : IUdpProxyTransport, IUdpExchangeCounters
{
    private int _sent;

    public IPEndPoint PeerEndpoint { get; } = new(IPAddress.Loopback, 50_000);
    public IPEndPoint LocalEndpoint { get; } = new(IPAddress.Loopback, localPort);

    /// <summary>The datagrams handed to this transport: the flush proof a scenario waits on.</summary>
    public int SentCount => Volatile.Read(ref _sent);

    public int DatagramsSent { get; set; }
    public bool SawResponse { get; set; }

    public ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _sent);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<UdpTransportReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("The benchmark receive should end through cancellation.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Creates <see cref="BenchmarkExchangeTransport"/> instances with distinct local ports (so the
/// relay-alias collision guard never rejects a distinct flow) and keeps the creation order, which is
/// what lets a scenario map its flow list to transports by index.
/// </summary>
internal sealed class BenchmarkExchangeTransportFactory : IUdpProxyTransportFactory
{
    private int _nextPort = 10_000;

    public List<BenchmarkExchangeTransport> Transports { get; } = [];

    public ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
    {
        var transport = new BenchmarkExchangeTransport(Interlocked.Increment(ref _nextPort));
        Transports.Add(transport);
        return ValueTask.FromResult<IUdpProxyTransport>(transport);
    }
}

internal sealed class NoopUdpResponseSink : IUdpResponseSink
{
    public static readonly NoopUdpResponseSink Instance = new();
    private NoopUdpResponseSink() { }
    public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
