using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Core.Tests;

/// <summary>
/// In-memory fakes for the SOCKS5 UDP relay transport seam: a factory emitting distinct bound
/// sockets, a channel-backed transport, a recording response sink, and an always-faulting pair.
/// </summary>
internal sealed class FakeTransportFactory : IUdpProxyTransportFactory
{
    private readonly AddressFamily _addressFamily;
    public List<FakeTransport> Transports { get; } = [];
    private int _nextLocalPort = 40000;
    private int _createCalls;

    public FakeTransportFactory(AddressFamily addressFamily = AddressFamily.InterNetwork)
    {
        _addressFamily = addressFamily;
    }

    public int CreateCalls => Volatile.Read(ref _createCalls);

    public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        // Each transport models a distinct bound UDP socket, so its local port is unique; the
        // relay alias collision guard in UdpProxyCoordinator must not reject distinct flows.
        Interlocked.Increment(ref _createCalls);
        var transport = new FakeTransport(_addressFamily, Interlocked.Increment(ref _nextLocalPort));
        lock (Transports) Transports.Add(transport);
        return ValueTask.FromResult<IUdpProxyTransport>(transport);
    }
}

internal sealed class FakeTransport : IUdpProxyTransport
{
    public FakeTransport(AddressFamily addressFamily, int localPort)
    {
        var loopback = addressFamily == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
        LocalEndpoint = new IPEndPoint(loopback, localPort);
        RelayEndpoint = new IPEndPoint(loopback, 50000);
    }

    public IPEndPoint RelayEndpoint { get; }
    public IPEndPoint LocalEndpoint { get; }
    public bool IsDisposed { get; private set; }
    public List<(IPEndPoint Destination, byte[] Payload)> Sent { get; } = [];
    public Channel<Socks5UdpReceiveResult> Received { get; } = Channel.CreateUnbounded<Socks5UdpReceiveResult>();

    /// <summary>Queues a valid decoded relay datagram for the session's receive loop.</summary>
    public void EnqueueResponse(Socks5UdpDatagram datagram) => Received.Writer.TryWrite(Socks5UdpReceiveResult.Received(datagram));

    /// <summary>Queues a per-datagram anomaly the real transport would surface as a skip result.</summary>
    public void EnqueueSkip(Socks5UdpReceiveSkipReason reason) => Received.Writer.TryWrite(Socks5UdpReceiveResult.Skipped(reason));

    public ValueTask SendAsync(IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        lock (Sent) Sent.Add((destination, payload.ToArray()));
        return ValueTask.CompletedTask;
    }

    public ValueTask<Socks5UdpReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        // A disposed transport models a closed socket: the pump's pending receive must end
        // promptly instead of blocking forever, mirroring the real socket's throw.
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return Received.Reader.ReadAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        Received.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Records every injected response so relay-pump tests can assert flow identity and payload.</summary>
internal sealed class FakeResponseSink : IUdpResponseSink
{
    public Channel<(FlowKey Flow, Endpoint Remote, byte[] Payload, byte[]? ClientMac)> Responses { get; } = Channel.CreateUnbounded<(FlowKey, Endpoint, byte[], byte[]?)>();

    public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, byte[]? clientMac, CancellationToken cancellationToken) =>
        Responses.Writer.WriteAsync((originalFlow, remoteSource, payload.ToArray(), clientMac), cancellationToken);
}

/// <summary>Fails the first receive immediately; used to prove session removal fires without another send.</summary>
internal sealed class ImmediateFaultTransportFactory : IUdpProxyTransportFactory
{
    private int _calls;
    public List<ImmediateFaultTransport> FaultedTransports { get; } = [];

    public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) == 1)
        {
            var transport = new ImmediateFaultTransport(AddressFamily.InterNetwork, 40000);
            FaultedTransports.Add(transport);
            return ValueTask.FromResult<IUdpProxyTransport>(transport);
        }

        return ValueTask.FromResult<IUdpProxyTransport>(new FakeTransport(AddressFamily.InterNetwork, 40001));
    }
}

internal sealed class ImmediateFaultTransport : IUdpProxyTransport
{
    public ImmediateFaultTransport(AddressFamily addressFamily, int localPort)
    {
        var loopback = addressFamily == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
        LocalEndpoint = new IPEndPoint(loopback, localPort);
        RelayEndpoint = new IPEndPoint(loopback, 50000);
    }

    public IPEndPoint RelayEndpoint { get; }
    public IPEndPoint LocalEndpoint { get; }
    public bool IsDisposed { get; private set; }

    public ValueTask SendAsync(IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        ValueTask.FromException(new IOException("relay receive already failed"));

    public ValueTask<Socks5UdpReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        ValueTask.FromException<Socks5UdpReceiveResult>(new IOException("relay receive failed"));

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
