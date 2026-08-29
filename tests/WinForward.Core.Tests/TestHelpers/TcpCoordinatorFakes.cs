using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.FrameBuilders;

namespace WinForward.Core.Tests;

/// <summary>
/// Shared fakes, packet builders, and the dispatcher harness for the TcpProxyCoordinator
/// test suite (Lifecycle / Capacity / Rewrite / Concurrency test classes).
/// </summary>
internal static class TcpCoordinatorFakes
{
    private static readonly IPAddress ClientIpv4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress DestIpv4 = IPAddress.Parse("192.0.2.53");

    internal static CapturedFlowPacket MakeSynPacket(IPAddress client, IPAddress destination, ushort clientPort, ushort destinationPort, byte[]? payload = null)
    {
        var frame = client.AddressFamily == AddressFamily.InterNetwork
            ? BuildIpv4TcpSyn(client, destination, clientPort, destinationPort, payload)
            : BuildIpv6TcpSyn(client, destination, clientPort, destinationPort);
        var local = Endpoint.From(client, clientPort);
        var remote = Endpoint.From(destination, destinationPort);
        var key = FlowKey.Create(local, remote, TransportProtocol.Tcp, FlowOriginKind.Host);
        var context = new FlowContext(key, "app.exe", null, null, "eth0", destinationPort);
        var lease = new PacketLease(frame);
        return new CapturedFlowPacket(lease, context, new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 0x1234));
    }

internal static CapturedFlowPacket MakeForwardedSynPacket(IPAddress client, IPAddress destination, ushort clientPort, ushort destinationPort, Action<byte[]>? mutateFrame = null)
{
    var frame = client.AddressFamily == AddressFamily.InterNetwork
        ? BuildIpv4TcpSyn(client, destination, clientPort, destinationPort)
        : BuildIpv6TcpSyn(client, destination, clientPort, destinationPort);
    mutateFrame?.Invoke(frame);
    var local = Endpoint.From(client, clientPort);
    var remote = Endpoint.From(destination, destinationPort);
    var adapter = new AdapterContext("veth-1", "vEthernet 1", 7);
    var key = FlowKey.Create(local, remote, TransportProtocol.Tcp, FlowOriginKind.Forwarded, adapter);
    var context = new FlowContext(key, null, null, "veth-1", "vEthernet 1", destinationPort);
    var lease = new PacketLease(frame);
    return new CapturedFlowPacket(lease, context, new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnReceive, 0x1234));
}

internal static CapturedFlowPacket MakeReversePacketClassifierOrientation(IPAddress source, ushort sourcePort, IPAddress destination, ushort destinationPort, nint adapterHandle = 0x1234, Action<byte[]>? mutateFrame = null, byte[]? payload = null)
{
    var frame = source.AddressFamily == AddressFamily.InterNetwork
        ? BuildIpv4TcpSyn(source, destination, sourcePort, destinationPort, payload)
        : BuildIpv6TcpSyn(source, destination, sourcePort, destinationPort);
    mutateFrame?.Invoke(frame);
    var local = Endpoint.From(source, sourcePort);
    var remote = Endpoint.From(destination, destinationPort);
    var key = FlowKey.Create(local, remote, TransportProtocol.Tcp, FlowOriginKind.Host);
    var context = new FlowContext(key, "app.exe", null, null, "eth0", destinationPort);
    var lease = new PacketLease(frame);
    return new CapturedFlowPacket(lease, context, new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnReceive, adapterHandle));
}

internal const byte TcpFlagAck = 0x10;
internal const byte TcpFlagFinAck = 0x11;

/// <summary>
/// Builds a forward (client -> server) TCP packet on the original tuple with arbitrary flags —
/// e.g. a straggler ACK/FIN-ACK arriving after teardown. The checksum is intentionally stale:
/// the tombstone path consumes the frame without ever rewriting it. The sequence number can be
/// set via <paramref name="mutateFrame"/> to emulate in-flight data.
/// </summary>
internal static CapturedFlowPacket MakeForwardTcpPacket(IPAddress client, IPAddress destination, ushort clientPort, ushort destinationPort, byte tcpFlags, byte[]? payload = null, Action<byte[]>? mutateFrame = null)
{
    var frame = BuildIpv4TcpSyn(client, destination, clientPort, destinationPort, payload);
    const int tcpFlagsOffset = 47;
    frame[tcpFlagsOffset] = tcpFlags;
    mutateFrame?.Invoke(frame);
    var local = Endpoint.From(client, clientPort);
    var remote = Endpoint.From(destination, destinationPort);
    var key = FlowKey.Create(local, remote, TransportProtocol.Tcp, FlowOriginKind.Host);
    var context = new FlowContext(key, "app.exe", null, null, "eth0", destinationPort);
    return new CapturedFlowPacket(new PacketLease(frame), context, new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 0x1234));
}

internal static FlowKey MakeHostFlowKey() => FlowKey.Create(Endpoint.From(ClientIpv4, 53000), Endpoint.From(DestIpv4, 443), TransportProtocol.Tcp, FlowOriginKind.Host);

internal static DispatcherHarness CreateDispatcherHarness()
{
    var listenerFactory = new FakeListenerFactory();
    var injector = new FakeInjector();
    var relayFactory = new CompletableRelayFactory();
    var logger = new RecordingRuntimeLogger();
    var selfTraffic = new SelfTrafficRegistry();
    var table = new TcpRedirectTable();
    var coordinator = new TcpProxyCoordinator(listenerFactory, relayFactory, injector, table, selfTraffic, new FakeLocalAddressProvider(), logger);

    var server = new Socks5Server("primary", "127.0.0.1", 1080, null, null);
    var servers = new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase) { [server.Name] = server };
    var rules = new[] { new PolicyRule(new RuleMatcher(), new FlowDecision(FlowAction.Proxy, 0, server.Name)) };
    var config = new ValidatedConfiguration(servers, new PolicySnapshot(rules, FlowAction.Block));
    var executor = new NdisPacketActionExecutor(new CountingReinjector(), logger, tcpProxy: coordinator);
    var dispatcher = new FlowDispatcher(
        config, selfTraffic, executor,
        reverseHandler: coordinator.HandleReverseIfApplicableAsync,
        fragmentHandler: coordinator.HandleFragmentAsync,
        logger: logger);
    return new DispatcherHarness(coordinator, listenerFactory, injector, relayFactory, table, dispatcher, logger);
}

internal static async Task EstablishRelayingSessionAsync(DispatcherHarness harness)
{
    await harness.Dispatcher.DispatchAsync(MakeSynPacket(ClientIpv4, DestIpv4, 53000, 443), CancellationToken.None);
    var listener = Assert.Single(harness.ListenerFactory.Listeners);
    await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(Endpoint.From(DestIpv4, 53000)), CancellationToken.None);
    await WaitForAsync(() => harness.RelayFactory.Relay is not null);
}
}

/// <summary>
/// A dispatcher wired to a real TCP coordinator over the standard fakes, for sweep-semantics
/// tests: the flow table, the redirect table, and the tombstones advance together exactly as
/// the runtime composition does.
/// </summary>
internal sealed record DispatcherHarness(
    TcpProxyCoordinator Coordinator,
    FakeListenerFactory ListenerFactory,
    FakeInjector Injector,
    CompletableRelayFactory RelayFactory,
    TcpRedirectTable Table,
    FlowDispatcher Dispatcher,
    RecordingRuntimeLogger Logger);

internal sealed class FakeListenerFactory : ITcpRedirectListenerFactory
{
    private readonly Endpoint? _fixedTuple;
    private readonly bool _throwOnCreate;
    private int _nextPort = 40000;

    public FakeListenerFactory(Endpoint? fixedTuple = null, bool throwOnCreate = false)
    {
        _fixedTuple = fixedTuple;
        _throwOnCreate = throwOnCreate;
    }

    public List<FakeListener> Listeners { get; } = [];
    public List<AddressFamilyKind> RequestedFamilies { get; } = [];

    public ValueTask<ITcpRedirectListener> CreateAsync(AddressFamilyKind addressFamily, CancellationToken cancellationToken)
    {
        if (_throwOnCreate) throw new IOException("listener allocation failed");
        RequestedFamilies.Add(addressFamily);
        var loopback = addressFamily == AddressFamilyKind.IPv4 ? IPAddress.Loopback : IPAddress.IPv6Loopback;
        var tuple = _fixedTuple ?? Endpoint.From(loopback, checked((ushort)Interlocked.Increment(ref _nextPort)));
        var listener = new FakeListener(tuple);
        lock (Listeners) Listeners.Add(listener);
        return ValueTask.FromResult<ITcpRedirectListener>(listener);
    }
}

internal sealed class BarrierListenerFactory(int participantCount) : ITcpRedirectListenerFactory
{
    /* Gates CreateAsync so the first participantCount-1 callers block until the last one
     * arrives; all are then released together. This guarantees every caller passed the
     * coordinator's pre-claim TryResolveByOriginal fast path (empty table) before any
     * TryClaim runs, deterministically forcing the redirect-table exactly-once race a
     * synchronous fake masks. */
    private readonly TaskCompletionSource _gate = new();
    private int _arrived;
    private int _nextPort = 40000;

    public List<FakeListener> Listeners { get; } = [];

    public async ValueTask<ITcpRedirectListener> CreateAsync(AddressFamilyKind addressFamily, CancellationToken cancellationToken)
    {
        // The last caller to arrive opens the gate; the rest were already awaiting it.
        if (Interlocked.Increment(ref _arrived) == participantCount) _gate.TrySetResult();
        await using var registration = cancellationToken.Register(() => _gate.TrySetCanceled(cancellationToken));
        await _gate.Task.ConfigureAwait(false);
        var loopback = addressFamily == AddressFamilyKind.IPv4 ? IPAddress.Loopback : IPAddress.IPv6Loopback;
        var listener = new FakeListener(Endpoint.From(loopback, checked((ushort)Interlocked.Increment(ref _nextPort))));
        lock (Listeners) Listeners.Add(listener);
        return listener;
    }
}

internal sealed class SingleListenerFactory(ITcpRedirectListener listener) : ITcpRedirectListenerFactory
{
    public ValueTask<ITcpRedirectListener> CreateAsync(AddressFamilyKind addressFamily, CancellationToken _) => ValueTask.FromResult(listener);
}

internal sealed class ThrowingListener : ITcpRedirectListener
{
    private int _acceptCount;
    public Endpoint TranslatedTuple => Endpoint.From(IPAddress.Loopback, 40000);
    public int AcceptCount => Volatile.Read(ref _acceptCount);
    public bool IsDisposed { get; private set; }

    public ValueTask<ITcpAcceptedConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _acceptCount);
        cancellationToken.ThrowIfCancellationRequested();
        throw new IOException("transient accept error");
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeListener(Endpoint translatedTuple) : ITcpRedirectListener
{
    public Endpoint TranslatedTuple { get; } = translatedTuple;
    public Channel<FakeAcceptedConnection> AcceptChannel { get; } = Channel.CreateUnbounded<FakeAcceptedConnection>();
    public bool IsDisposed { get; private set; }

    public async ValueTask<ITcpAcceptedConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        return await AcceptChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeAcceptedConnection(Endpoint remoteEndPoint) : ITcpAcceptedConnection
{
    public Endpoint RemoteEndPoint { get; } = remoteEndPoint;
    public bool IsDisposed { get; private set; }
    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeRelayFactory(bool throwOnEstablish = false) : ITcpProxyRelayFactory
{
    public List<Endpoint> EstablishedDestinations { get; } = [];

    public ValueTask<ITcpRelay> EstablishAsync(Endpoint originalDestination, ITcpAcceptedConnection acceptedConnection, Socks5Server server, CancellationToken cancellationToken)
    {
        if (throwOnEstablish) throw new IOException("relay setup failed");
        lock (EstablishedDestinations) EstablishedDestinations.Add(originalDestination);
        return ValueTask.FromResult<ITcpRelay>(new FakeRelay());
    }
}

internal sealed class FakeRelay : ITcpRelay
{
    public Task Completion { get; } = new TaskCompletionSource<bool>().Task;
    public bool IsDisposed { get; private set; }
    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeInjector(int? throwOnCall = null, bool throwIfCanceled = false, Exception? exception = null) : ITcpRedirectInjector
{
    public List<(byte[] Frame, bool TowardMstcp, nint AdapterHandle)> InjectedFrames { get; } = [];
    private int _calls;

    public ValueTask InjectAsync(ReadOnlyMemory<byte> rewrittenFrame, bool towardMstcp, nint adapterHandle, CancellationToken cancellationToken)
    {
        if (throwIfCanceled) cancellationToken.ThrowIfCancellationRequested();
        if (throwOnCall is int call && Interlocked.Increment(ref _calls) == call) throw exception ?? new IOException("injection failed");
        lock (InjectedFrames) InjectedFrames.Add((rewrittenFrame.ToArray(), towardMstcp, adapterHandle));
        return ValueTask.CompletedTask;
    }
}

internal sealed class GatedRelayFactory : ITcpProxyRelayFactory
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource EstablishStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public FakeRelay? CreatedRelay { get; private set; }

    public async ValueTask<ITcpRelay> EstablishAsync(Endpoint originalDestination, ITcpAcceptedConnection acceptedConnection, Socks5Server server, CancellationToken cancellationToken)
    {
        EstablishStarted.TrySetResult();
        await _release.Task.ConfigureAwait(false);
        var relay = new FakeRelay();
        CreatedRelay = relay;
        return relay;
    }

    public void Release() => _release.TrySetResult();
}

internal sealed class GatedListenerFactory : ITcpRedirectListenerFactory
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<FakeListener> _listeners = [];

    public TaskCompletionSource CreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<FakeListener> Listeners => _listeners;

    public async ValueTask<ITcpRedirectListener> CreateAsync(AddressFamilyKind addressFamily, CancellationToken cancellationToken)
    {
        CreateStarted.TrySetResult();
        await _release.Task.ConfigureAwait(false);
        var address = addressFamily == AddressFamilyKind.IPv4 ? IPAddress.Loopback : IPAddress.IPv6Loopback;
        var listener = new FakeListener(Endpoint.From(address, 42000));
        lock (_listeners) _listeners.Add(listener);
        return listener;
    }

    public void Release() => _release.TrySetResult();
}

internal sealed class CompletableRelay : ITcpRelay
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completion.Task;
    public bool IsDisposed { get; private set; }

    public void Complete() => _completion.TrySetResult();

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class CompletableRelayFactory : ITcpProxyRelayFactory
{
    public CompletableRelay? Relay { get; private set; }

    public ValueTask<ITcpRelay> EstablishAsync(Endpoint originalDestination, ITcpAcceptedConnection acceptedConnection, Socks5Server server, CancellationToken cancellationToken)
    {
        Relay = new CompletableRelay();
        return ValueTask.FromResult<ITcpRelay>(Relay);
    }
}
