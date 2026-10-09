using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.Logging;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Runtime.Socks5;

/// <summary>
/// The collaborators every per-flow association is built from: the loop-prevention registry the
/// control tuple is registered in before the SYN leaves the host, the shared SOCKS5 address cache,
/// the dial seam, the logger, and the association-lost log throttle the factory owns for every
/// association it creates.
/// </summary>
internal readonly record struct Socks5UdpAssociationContext(
    SelfTrafficRegistry SelfTraffic,
    Socks5AddressCache? AddressCache,
    Func<Socks5Server, CancellationToken, ValueTask<Socks5ControlConnection>>? CreateControl,
    ILogger Logger,
    RuntimeLogThrottle LostLog);

/// <summary>
/// One flow's own authenticated SOCKS5 UDP association: the control connection it dialed, the
/// <c>UDP ASSOCIATE</c> result with its relay publication, and the watchdog on the control stream.
/// It lives and dies with the flow — there is no pool, no lease refcount, no warm retention, and no
/// in-place re-association.
/// <para>
/// Death is explicit: the watchdog blocks on the control stream, and a server-side close (a 0-byte
/// read) or a stream fault stores a <see cref="UdpAssociationLostException"/> carrying the death as
/// its inner exception. The association's owning <see cref="Socks5UdpTransport"/> refuses every
/// later datagram with that exception, the coordinator removes the flow's slot as
/// <c>UdpTeardownReason.AssociationLost</c> without arming the setup cooldown, and the flow
/// re-establishes on its next datagram with a fresh association.
/// </para>
/// <para>
/// The association is an owner in the <see cref="QuiescenceScope"/> sense: the watchdog is its
/// scope child, so <see cref="DisposeAsync"/> seals the scope synchronously and only then closes
/// the control connection. A sealed scope records no fault for the read it just faulted, which is
/// what makes teardown distinguishable from death.
/// </para>
/// </summary>
internal sealed class Socks5UdpAssociation : IAsyncDisposable
{
    private readonly Socks5UdpAssociationContext _context;
    private readonly QuiescenceScope _scope = new();

    private Socks5ControlConnection? _control;
    private IPEndPoint? _relayEndpoint;
    private SocketAddress? _relaySocketAddress;
    private UdpAssociationLostException? _fault;
    private int _disposeStarted;

    private Socks5UdpAssociation(Socks5Server server, Socks5UdpAssociationContext context)
    {
        Server = server;
        _context = context;
    }

    /// <summary>The server this association is authenticated against; the <c>udp.association.lost</c> event's proxy field.</summary>
    private Socks5Server Server { get; }

    /// <summary>
    /// The relay endpoint the server returned from <c>UDP ASSOCIATE</c>: the flow's send target, its
    /// reply-source validation witness, and the router of its loop-prevention tuple. Published once,
    /// before the association is handed to its transport, and never replaced.
    /// </summary>
    internal IPEndPoint RelayEndpoint => Volatile.Read(ref _relayEndpoint)
        ?? throw new InvalidOperationException("The UDP association has no negotiated relay endpoint.");

    /// <summary>The relay endpoint serialized once for the allocation-free send path.</summary>
    internal SocketAddress RelaySocketAddress => Volatile.Read(ref _relaySocketAddress)
        ?? throw new InvalidOperationException("The UDP association has no negotiated relay endpoint.");

    internal AddressFamily RelayAddressFamily => RelayEndpoint.AddressFamily;

    /// <summary>
    /// The association's death, or null while its control stream is alive. Typed as the exception
    /// the transport throws, so the fail-closed check on the datagram path is one null test.
    /// </summary>
    internal UdpAssociationLostException? Fault => Volatile.Read(ref _fault);

    private bool IsFaulted => Volatile.Read(ref _fault) is not null;

    /// <summary>
    /// Dials an authenticated control connection, sends <c>UDP ASSOCIATE</c>, publishes the relay
    /// the server returned, and starts the control-stream watchdog. A failure at any step closes
    /// whatever was acquired and throws to the caller, so the flow fails its setup closed instead of
    /// holding a half-built association.
    /// </summary>
    internal static async ValueTask<Socks5UdpAssociation> ConnectAsync(Socks5Server server, Socks5UdpAssociationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        var association = new Socks5UdpAssociation(server, context);
        Socks5ControlConnection? control = null;
        try
        {
            control = await association.DialAsync(cancellationToken).ConfigureAwait(false);
            var relay = await control.UdpAssociateAsync(cancellationToken).ConfigureAwait(false);
            // Published before the control connection is adopted, so the watchdog (started below)
            // and the transport that reads it can never observe a live association without a relay.
            Volatile.Write(ref association._relayEndpoint, relay);
            Volatile.Write(ref association._relaySocketAddress, relay.Serialize());
            Volatile.Write(ref association._control, control);
            control = null;
            association.StartWatchdog();
            return association;
        }
        catch
        {
            await association.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (control is not null) await control.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes the control connection and joins the watchdog exactly once, through every path. The
    /// seal happens before the close: the watchdog's read then faults as part of a teardown the
    /// scope has already sealed, which the watchdog reports as a normal exit rather than as the
    /// association's death.
    /// </summary>
    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref _disposeStarted, 1) != 0
            ? new ValueTask(_scope.DrainAsync())
            : new ValueTask(DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        var drain = _scope.DrainAsync();
        try
        {
            var control = Interlocked.Exchange(ref _control, value: null);
            if (control is not null) await control.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await drain.ConfigureAwait(false);
        }
    }

    private ValueTask<Socks5ControlConnection> DialAsync(CancellationToken cancellationToken)
    {
        if (_context.CreateControl is { } createControl) return createControl(Server, cancellationToken);
        var selfTraffic = _context.SelfTraffic;
        return Socks5ControlConnection.ConnectAsync(
            Server,
            cancellationToken,
            (local, remote) => selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(
                TransportProtocol.Tcp,
                Endpoint.From(local.Address, checked((ushort)local.Port)),
                Endpoint.From(remote.Address, checked((ushort)remote.Port)))),
            addressCache: _context.AddressCache);
    }

    private void StartWatchdog()
    {
        // A sealed scope refuses the child; the association is being torn down, which closes the very
        // control connection the watchdog would have watched.
        _scope.Run(WatchAsync, "udp.association.watch");
    }

    /// <summary>
    /// The watchdog: blocks on the control stream and treats a server-side close (a 0-byte read) or
    /// a stream fault as the association's death. RFC 1928 defines no control-connection traffic
    /// after <c>UDP ASSOCIATE</c>, so any received byte is not death and the read continues. Our own
    /// cancellation and disposal are a normal exit — never a fault.
    /// <para>
    /// Any other fault is death too: nothing else observes this child, and an escaping exception
    /// would leave the transport sending to a relay no one is watching. Recording the death here is
    /// the watchdog's designed exit; the scope owns the child, so the fault can never surface as an
    /// unobserved task exception.
    /// </para>
    /// </summary>
    private async Task WatchAsync(CancellationToken token)
    {
        try
        {
            var control = Volatile.Read(ref _control);
            if (control is null) return;
            var death = await AwaitControlDeathAsync(control, token).ConfigureAwait(false);
            if (death is null || token.IsCancellationRequested || _scope.IsSealed || IsFaulted) return;
            await FaultAsync(death).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFaulted && (exception is not OperationCanceledException || !token.IsCancellationRequested))
        {
            await FaultAsync(new IOException("The SOCKS5 UDP association watchdog faulted; the association is no longer watched.", exception)).ConfigureAwait(false);
        }
    }

    private static async ValueTask<Exception?> AwaitControlDeathAsync(Socks5ControlConnection control, CancellationToken token)
    {
        var stream = control.GetUpstreamStream();
        var probe = new byte[1];
        try
        {
            while (true)
            {
                if (await stream.ReadAsync(probe, token).ConfigureAwait(false) == 0)
                {
                    return new EndOfStreamException("The SOCKS5 control connection closed; the UDP association ended.");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            return exception;
        }
    }

    /// <summary>
    /// Records the association's death once, reports it on the flow's own log, and closes the dead
    /// control connection so its descriptor is not held until the transport is disposed. The close
    /// happens before the reference is given up: a dispose that throws leaves the connection reachable
    /// for the association's own teardown to retry instead of stranding a live socket with no owner.
    /// The counter the coordinator moves (<c>udpAssociationLost</c>) belongs to the send path that
    /// surfaces this fault, so it is deliberately not touched here.
    /// </summary>
    private async ValueTask FaultAsync(Exception death)
    {
        // The watchdog is this association's only writer, so a plain guarded publication is enough:
        // only the first death is recorded, reported, and closed.
        if (Volatile.Read(ref _fault) is not null) return;
        var lost = new UdpAssociationLostException("The flow's SOCKS5 UDP association was lost; the flow must be re-established.", death);
        Volatile.Write(ref _fault, lost);
        // One line per window across every association of this factory: a server that drops a
        // whole population at once is one incident, and the cumulative counter is the measurement.
        if (_context.LostLog.ShouldEmit())
        {
            var reason = death.GetType().Name;
            UdpProxyLog.UdpAssociationLost(_context.Logger, Server.Name, RelayEndpoint, reason);
        }

        var control = Volatile.Read(ref _control);
        if (control is null) return;
        await control.DisposeAsync().ConfigureAwait(false);
        // Only a completed close clears the reference. A teardown that took it first joins the same
        // one-shot close; one that arrives after this exchange still finds the reference and retries.
        Interlocked.CompareExchange(ref _control, value: null, control);
    }
}
