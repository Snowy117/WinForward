using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using WinForward.Configuration;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Core.Tests;

/// <summary>
/// In-memory fakes for the SOCKS5 UDP relay transport seam: a factory emitting distinct bound
/// sockets, a channel-backed transport, a recording response sink, and an always-faulting pair.
/// </summary>
internal sealed class FakeTransportFactory(AddressFamily addressFamily = AddressFamily.InterNetwork) : IUdpProxyTransportFactory
{
    public List<FakeTransport> Transports { get; } = [];
    private int _nextLocalPort = 40000;
    private int _createCalls;

    public int CreateCalls => Volatile.Read(ref _createCalls);

    public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        // Each transport models a distinct bound UDP socket, so its local port is unique; the
        // relay alias collision guard in UdpProxyCoordinator must not reject distinct flows.
        Interlocked.Increment(ref _createCalls);
        var transport = new FakeTransport(addressFamily, Interlocked.Increment(ref _nextLocalPort));
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
    public List<(Endpoint Destination, byte[] Payload)> Sent { get; } = [];
    public Channel<Socks5UdpReceiveResult> Received { get; } = Channel.CreateUnbounded<Socks5UdpReceiveResult>();

    /// <summary>
    /// Opt-in hold for <see cref="SendSpanAsync"/>: when set, the send completes only once the test
    /// completes the gate, so a test can observe an outstanding session send lease. Null by default
    /// (every other test's send completes synchronously).
    /// </summary>
    public TaskCompletionSource? SendGate { get; init; }

    /// <summary>
    /// Opt-in hold for <see cref="DisposeAsync"/>: when set, disposal reports itself started and
    /// then waits for the gate, so a test can park a session's teardown mid-flight. Null by default.
    /// </summary>
    public TaskCompletionSource? DisposeGate { get; set; }

    /// <summary>Completes once <see cref="DisposeAsync"/> was entered (before the optional gate).</summary>
    public TaskCompletionSource<bool> DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, <see cref="DisposeAsync"/> faults with it while still marking the transport disposed.</summary>
    public Exception? DisposeFault { get; set; }

    /// <summary>Queues a valid decoded relay datagram for the session's receive loop.</summary>
    public void EnqueueResponse(Socks5UdpDatagram datagram) => Received.Writer.TryWrite(Socks5UdpReceiveResult.Received(datagram));

    /// <summary>Queues a per-datagram anomaly the real transport would surface as a skip result.</summary>
    public void EnqueueSkip(Socks5UdpReceiveSkipReason reason) => Received.Writer.TryWrite(Socks5UdpReceiveResult.Skipped(reason));

    public ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
    {
        lock (Sent) Sent.Add((destination, payload.ToArray()));
        return SendGate is not null ? new ValueTask(SendGate.Task) : ValueTask.CompletedTask;
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
        DisposeStarted.TrySetResult(true);
        if (DisposeFault is { } fault) return ValueTask.FromException(fault);
        return DisposeGate is { } gate ? new ValueTask(gate.Task) : ValueTask.CompletedTask;
    }
}

/// <summary>Records every injected response so relay-pump tests can assert flow identity and payload.</summary>
internal sealed class FakeResponseSink : IUdpResponseSink
{
    public Channel<(FlowKey Flow, Endpoint Remote, byte[] Payload, MacAddress ClientMac)> Responses { get; } = Channel.CreateUnbounded<(FlowKey, Endpoint, byte[], MacAddress)>();

    public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken cancellationToken) =>
        Responses.Writer.WriteAsync((originalFlow, remoteSource, payload.ToArray(), clientMac), cancellationToken);
}

/// <summary>A sink that accepts every response without recording: for tests that exercise a coordinator but never assert on responses.</summary>
internal sealed class NoopResponseSink : IUdpResponseSink
{
    public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
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

    public ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken) =>
        ValueTask.FromException(new IOException("relay receive already failed"));

    public ValueTask<Socks5UdpReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        ValueTask.FromException<Socks5UdpReceiveResult>(new IOException("relay receive failed"));

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
