using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.TestSupport;

/// <summary>
/// The relays and plumbing the UDP association suites share: a permissive relay that echoes every
/// datagram, a source-port-pinning relay that answers only the first client port it reads, the
/// transport scope that releases its lease before the pool, and the pool/transport/receive helpers.
/// </summary>
internal static class UdpAssociationFakes
{
    /// <summary>
    /// Starts a pool in the given mode with an optional recording logger and optional placement
    /// bounds; the caller owns it. Omitted bounds are the pool's own defaults.
    /// </summary>
    internal static UdpAssociationPool CreatePool(
        UdpAssociationReuseMode mode,
        IRuntimeLogger? logger = null,
        int maxAssociationsPerServer = UdpAssociationPool.DefaultMaxAssociationsPerServer,
        int flowsPerAssociation = UdpAssociationPool.DefaultFlowsPerAssociation) =>
        new(
            new SelfTrafficRegistry(),
            mode,
            logger: logger,
            maxAssociationsPerServer: maxAssociationsPerServer,
            flowsPerAssociation: flowsPerAssociation);

    /// <summary>Rents one lease and wraps it in a transport, so a failed assertion cannot strand a holder.</summary>
    internal static TransportScope CreateTransport(UdpAssociationPool pool, SelfTrafficRegistry registry, ScriptedSocks5UdpServer server) =>
        new(pool, registry, server.Server);

    /// <summary>A bound loopback UDP socket the tests use as a relay endpoint.</summary>
    internal static Socket NewCapture()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    /// <summary>
    /// Receives one decoded relay datagram, or false when none arrives inside the observation
    /// window — a real transport receive has no timeout, so an unanswered flow would hang the test.
    /// </summary>
    internal static async Task<bool> ReceiveDatagramAsync(Socks5UdpTransport transport)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await transport.ReceiveAsync(new byte[256], budget.Token);
        return result.HasDatagram;
    }
}

/// <summary>
/// A relay that echoes every datagram back to the source it came from: the permissive server
/// shape, where any attached flow's replies come back and the sampler must confirm sharing.
/// </summary>
internal sealed class EchoRelay : IAsyncDisposable
{
    private readonly Socket _socket = UdpAssociationFakes.NewCapture();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;

    public EchoRelay() => _loop = EchoAsync(_shutdown.Token);

    public IPEndPoint Endpoint => (IPEndPoint)_socket.LocalEndPoint!;

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _socket.Dispose();
        await _loop.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task EchoAsync(CancellationToken token)
    {
        var buffer = new byte[65_535];
        while (!token.IsCancellationRequested)
        {
            EndPoint source = new IPEndPoint(IPAddress.Any, 0);
            SocketReceiveFromResult received;
            try
            {
                received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, source, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = await _socket.SendToAsync(buffer.AsMemory(0, received.ReceivedBytes), SocketFlags.None, received.RemoteEndPoint, token).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A relay that answers only the first source port it ever sees, the way a source-port-pinning
/// SOCKS5 server does: a second flow multiplexed onto one association sends into the void while
/// the first flow keeps being answered. <see cref="PumpAsync"/> returns the number of distinct
/// client ports it read datagrams from, the test's proof that the two flows were really
/// multiplexed onto this one relay.
/// </summary>
internal sealed class PinningRelay : IAsyncDisposable
{
    private readonly Socket _socket = UdpAssociationFakes.NewCapture();
    private readonly HashSet<int> _sourcePorts = [];
    private int _pinnedPort;

    public IPEndPoint Endpoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>
    /// Reads exactly <paramref name="count"/> client datagrams, answering only the pinned port,
    /// and returns the number of distinct client ports seen. The relay is read only here, so the
    /// datagrams the test produced cannot be consumed out from under its assertions; the budget
    /// turns a missing datagram into a failure instead of a hang.
    /// </summary>
    public async Task<int> PumpAsync(int count)
    {
        var buffer = new byte[65_535];
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var index = 0; index < count; index++)
        {
            EndPoint source = new IPEndPoint(IPAddress.Any, 0);
            var received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, source, budget.Token).ConfigureAwait(false);
            if (received.RemoteEndPoint is not IPEndPoint remote) continue;
            if (IsPinnedAway(remote.Port)) continue;
            _ = await _socket.SendToAsync(buffer.AsMemory(0, received.ReceivedBytes), SocketFlags.None, remote, CancellationToken.None).ConfigureAwait(false);
        }

        lock (_sourcePorts) return _sourcePorts.Count;
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Records the client port and reports whether it is not the one this relay answers.</summary>
    private bool IsPinnedAway(int clientPort)
    {
        lock (_sourcePorts)
        {
            _sourcePorts.Add(clientPort);
            if (_pinnedPort == 0) _pinnedPort = clientPort;
            return clientPort != _pinnedPort;
        }
    }
}

/// <summary>
/// A transport plus the association it borrowed. Disposing releases the transport (and with it
/// its lease) before the pool, so a failed assertion cannot strand a holder in the pool's drain.
/// </summary>
internal sealed class TransportScope : IAsyncDisposable
{
    public TransportScope(UdpAssociationPool pool, SelfTrafficRegistry registry, Socks5Server server)
    {
        Lease = pool.RentAsync(server, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Transport = Socks5UdpTransport.Create(Lease, registry);
    }

    /// <summary>The borrowed lease, so a test can assert the association state behind the transport.</summary>
    public UdpAssociationLease Lease { get; }

    public Socks5UdpTransport Transport { get; }

    public async ValueTask DisposeAsync()
    {
        await Transport.DisposeAsync().ConfigureAwait(false);
        await Lease.DisposeAsync().ConfigureAwait(false);
    }
}
