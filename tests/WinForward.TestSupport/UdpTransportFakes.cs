using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.UdpProxy;

namespace WinForward.TestSupport;

/// <summary>
/// In-memory fakes for the SOCKS5 UDP relay transport seam: a factory emitting distinct bound
/// sockets, a channel-backed transport, a recording response sink, and an always-faulting pair.
/// </summary>
internal sealed class FakeTransportFactory(AddressFamily addressFamily = AddressFamily.InterNetwork) : IUdpProxyTransportFactory
{
    private readonly Lock _gate = new();
    private readonly List<FakeTransport> _transports = [];
    private int _nextLocalPort = 40000;
    private int _createCalls;

    /// <summary>Snapshot taken under the writer's lock: the setup worker publishes from its own thread, and <see cref="List{T}.Add"/> makes a count visible before its element.</summary>
    public IReadOnlyList<FakeTransport> Transports
    {
        get
        {
            lock (_gate) return [.. _transports];
        }
    }

    public int CreateCalls => Volatile.Read(ref _createCalls);

    public ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
    {
        // Each transport models a distinct bound UDP socket, so its local port is unique; the
        // relay alias collision guard in UdpProxyCoordinator must not reject distinct flows.
        Interlocked.Increment(ref _createCalls);
        var transport = new FakeTransport(addressFamily, Interlocked.Increment(ref _nextLocalPort));
        lock (_gate) _transports.Add(transport);
        return ValueTask.FromResult<IUdpProxyTransport>(transport);
    }
}

internal sealed class FakeTransport : IUdpProxyTransport
{
    public FakeTransport(AddressFamily addressFamily, int localPort)
    {
        var loopback = addressFamily == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
        LocalEndpoint = new IPEndPoint(loopback, localPort);
        PeerEndpoint = new IPEndPoint(loopback, 50000);
    }

    public IPEndPoint PeerEndpoint { get; }
    public IPEndPoint LocalEndpoint { get; }
    public bool IsDisposed { get; private set; }
    public List<(Endpoint Destination, byte[] Payload)> Sent { get; } = [];
    public Channel<UdpTransportReceiveResult> Received { get; } = Channel.CreateUnbounded<UdpTransportReceiveResult>();

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

    /// <summary>When set, <see cref="SendSpanAsync"/> faults with it instead of recording the datagram (a dead relay).</summary>
    public Exception? SendFault { get; init; }

    /// <summary>Queues a valid decoded relay datagram for the session's receive loop.</summary>
    public void EnqueueResponse(UdpTransportDatagram datagram) => Received.Writer.TryWrite(UdpTransportReceiveResult.Received(datagram));

    /// <summary>Queues a per-datagram anomaly the real transport would surface as a skip result.</summary>
    public void EnqueueSkip(UdpTransportSkipReason reason) => Received.Writer.TryWrite(UdpTransportReceiveResult.Skipped(reason));

    public ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
    {
        if (SendFault is not null) return ValueTask.FromException(SendFault);
        lock (Sent) Sent.Add((destination, payload.ToArray()));
        return SendGate is not null ? new ValueTask(SendGate.Task) : ValueTask.CompletedTask;
    }

    public ValueTask<UdpTransportReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
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

/// <summary>
/// Wraps any factory and records every transport it handed out, in creation order. Facts that need to
/// observe a transport the coordinator created (its socket shape, its exchange evidence) drive the
/// production factory through this decorator.
/// </summary>
internal sealed class RecordingTransportFactory(IUdpProxyTransportFactory inner) : IUdpProxyTransportFactory
{
    private readonly List<IUdpProxyTransport> _transports = [];
    private readonly Lock _gate = new();
    private int _createCalls;

    internal int CreateCalls => Volatile.Read(ref _createCalls);

    internal IReadOnlyList<IUdpProxyTransport> Transports
    {
        get
        {
            lock (_gate) return [.. _transports];
        }
    }

    public async ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
    {
        _ = Interlocked.Increment(ref _createCalls);
        var transport = await inner.CreateAsync(target, cancellationToken).ConfigureAwait(false);
        lock (_gate) _transports.Add(transport);
        return transport;
    }
}

/// <summary>Fails the first receive immediately; used to prove session removal fires without another send.</summary>
internal sealed class ImmediateFaultTransportFactory : IUdpProxyTransportFactory
{
    private readonly Lock _gate = new();
    private readonly List<ImmediateFaultTransport> _faulted = [];
    private int _calls;

    /// <summary>Snapshot of the faulted transports, taken under the writer's lock (see <see cref="FakeTransportFactory.Transports"/>).</summary>
    public IReadOnlyList<ImmediateFaultTransport> FaultedTransports
    {
        get
        {
            lock (_gate) return [.. _faulted];
        }
    }

    public ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) == 1)
        {
            var transport = new ImmediateFaultTransport(AddressFamily.InterNetwork, 40000);
            lock (_gate) _faulted.Add(transport);
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
        PeerEndpoint = new IPEndPoint(loopback, 50000);
    }

    public IPEndPoint PeerEndpoint { get; }
    public IPEndPoint LocalEndpoint { get; }
    public bool IsDisposed { get; private set; }

    public ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken) =>
        ValueTask.FromException(new IOException("relay receive already failed"));

    public ValueTask<UdpTransportReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        ValueTask.FromException<UdpTransportReceiveResult>(new IOException("relay receive failed"));

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
