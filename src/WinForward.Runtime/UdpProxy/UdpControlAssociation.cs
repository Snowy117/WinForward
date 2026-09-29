using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.Socks5;

namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// One atomic publication of a negotiated relay: the endpoint and its serialized form, replaced
/// together by an in-place re-association. A sender compares this reference (the change signal) and
/// then reads the endpoint from the same instance, so two re-associations landing between those
/// reads can no longer pair one relay's address with the next relay's endpoint.
/// </summary>
internal sealed record UdpRelayTarget(IPEndPoint Endpoint, SocketAddress SocketAddress);

/// <summary>
/// One authenticated SOCKS5 control connection plus its UDP ASSOCIATE result, serving up to
/// <see cref="UdpAssociationPool.FlowsPerAssociationLimit"/> concurrent flows — the configured
/// <c>udpAssociationFlowsPerAssociation</c> bound. Every flow still owns its own relay socket, so
/// reverse routing is unchanged; only the control connection is shared.
/// <para>
/// The association owns the control connection, its lease refcount, the watchdog that detects
/// association death (the control stream ending), and the in-place re-association that keeps every
/// attached flow's socket and session alive when only the relay endpoint moved. Attached flows read
/// the current relay endpoint through their lease, so a successful re-association is visible
/// without touching a single transport.
/// </para>
/// <para>
/// Placement state (<see cref="Recovering"/>, refcount consumers, the faulted set) is guarded by
/// the owning pool's gate: only <see cref="UdpAssociationPool"/> mutates
/// <see cref="Recovering"/>, and only while holding that gate. The lock-free readers — leases and
/// the watchdog — go through the volatile fields.
/// </para>
/// <para>
/// The capability evidence of the attached flows is a live set owned per lease:
/// <see cref="StartLease"/> adds the flow's record and <see cref="ReleaseLeaseAsync"/> removes it,
/// both under <see cref="_evidenceGate"/>. That lock is a leaf — never held while taking the pool's
/// gate, while the pool's gate may be held while taking it (attach runs under the pool gate) — so
/// <see cref="SnapshotEvidence"/> returns records of leases attached at that instant and nothing
/// else, however many leases the association has served before. The sampler's rule needs a live
/// responding sibling (design §5), so a pinning server whose answered flow has already been released
/// is not detectable by this association; detection resumes with the next answered sibling. The limit
/// is bounded by the lease lifetime and the 5 s tick, and the verdict it feeds is per server and
/// sticky.
/// </para>
/// </summary>
internal sealed class UdpControlAssociation : IAsyncDisposable
{
    /// <summary>The default bounded budget of one re-association attempt (dial + ASSOCIATE).</summary>
    internal static readonly TimeSpan s_defaultRecoveryTimeout = TimeSpan.FromSeconds(5);

    private readonly UdpAssociationPool _owner;
    private readonly UdpAssociationContext _context;
    private readonly QuiescenceScope _scope;
    private readonly Lock _associateGate = new();
    private readonly Lock _evidenceGate = new();
    private readonly List<UdpAssociationEvidence> _liveEvidence = [];

    private Socks5ControlConnection? _control;
    private UdpRelayTarget? _relayTarget;
    private Task? _associateTask;
    private Exception? _fault;
    private long _idleSinceTicks;
    private int _leaseCount;
    private int _watchStarted;
    private int _disposeStarted;

    internal UdpControlAssociation(UdpAssociationPool owner, Socks5Server server, bool isPrivate)
    {
        _owner = owner;
        _context = owner.Context;
        Server = server;
        IsPrivate = isPrivate;
        _scope = new QuiescenceScope(owner.Token);
    }

    /// <summary>The server this association is authenticated against; the pool's per-server key.</summary>
    internal Socks5Server Server { get; }

    /// <summary>
    /// A private association is never handed to a second flow: it is created for <c>off</c> mode
    /// (today's per-flow behaviour) and for a flow that arrives when the per-server association cap
    /// is reached, and it is closed as soon as its last lease is released.
    /// </summary>
    internal bool IsPrivate { get; }

    /// <summary>True while a re-association attempt is in flight; placement skips the association (pool gate).</summary>
    internal bool Recovering { get; set; }

    /// <summary>
    /// The current relay publication — the endpoint and its serialized form behind one volatile
    /// reference. A reader that compares it against its cached instance and a reader that needs the
    /// endpoint both observe the same re-association.
    /// </summary>
    internal UdpRelayTarget RelayTarget => Volatile.Read(ref _relayTarget)
        ?? throw new InvalidOperationException("The UDP association has no negotiated relay endpoint.");

    internal IPEndPoint RelayEndpoint => RelayTarget.Endpoint;

    internal AddressFamily RelayAddressFamily => RelayTarget.Endpoint.AddressFamily;

    /// <summary>An unrecoverable association death; every attached lease reports it.</summary>
    internal bool IsFaulted => Volatile.Read(ref _fault) is not null;

    internal Exception? Fault => Volatile.Read(ref _fault);

    internal int LeaseCount => Volatile.Read(ref _leaseCount);

    /// <summary>
    /// Whether this association can serve one more flow, under the pool's configured flow bound.
    /// Placement reads it under the pool gate; the refcount itself is interlocked because releases
    /// run outside that gate.
    /// </summary>
    internal bool IsAvailableForPlacement =>
        !IsFaulted && !Recovering && Volatile.Read(ref _leaseCount) < _owner.FlowsPerAssociationLimit;

    /// <summary>
    /// Dials and performs UDP ASSOCIATE at most once per association; concurrent first rents join
    /// the same attempt. A failed attempt discards the association (the next flow dials again), so a
    /// transient failure can never poison the server's warm set.
    /// </summary>
    internal Task EnsureAssociatedAsync(CancellationToken cancellationToken)
        => Volatile.Read(ref _associateTask) ?? StartAssociateAsync(cancellationToken);

    /// <summary>
    /// Attaches the flow's capability evidence and claims its lease refcount; the pool calls this
    /// while holding its gate and before the lease is returned. The evidence joins the live set here
    /// and leaves it in <see cref="ReleaseLeaseAsync"/>, so the sampler reads exactly the leases
    /// attached at the instant it samples.
    /// </summary>
    internal void StartLease(UdpAssociationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        lock (_evidenceGate) _liveEvidence.Add(evidence);
        Volatile.Write(ref _idleSinceTicks, 0);
        Interlocked.Increment(ref _leaseCount);
    }

    /// <summary>
    /// An independent copy of the attached leases' evidence, plus how many records it holds, for the
    /// sampler's 5 s maintenance tick (never the datagram path, so the copy is cold). The count is the
    /// copy's own length, so the sampler cannot walk past the evidence it was handed.
    /// </summary>
    internal (UdpAssociationEvidence[] Evidence, int Attached) SnapshotEvidence()
    {
        lock (_evidenceGate)
        {
            return _liveEvidence.Count == 0 ? ([], 0) : ([.. _liveEvidence], _liveEvidence.Count);
        }
    }

    /// <summary>
    /// Releases one flow lease. The flow's evidence leaves the live set before the refcount falls, so
    /// no later sample can read a released lease. A private or faulted association is closed by its
    /// last holder; a warm shared association is parked with its idle clock started, so
    /// <see cref="UdpAssociationPool"/> can reuse it and retire it once it has been idle for the
    /// retention window.
    /// </summary>
    internal async ValueTask ReleaseLeaseAsync(UdpAssociationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        lock (_evidenceGate) _ = _liveEvidence.Remove(evidence);
        if (Interlocked.Decrement(ref _leaseCount) > 0) return;
        if (IsPrivate || IsFaulted)
        {
            await DisposeAsync().ConfigureAwait(false);
            return;
        }

        Volatile.Write(ref _idleSinceTicks, _context.TimeProvider.GetUtcNow().UtcTicks);
    }

    /// <summary>Whether this association may be retired: no lease outstanding and idle for the retention window.</summary>
    internal bool CanRetire(DateTimeOffset now, TimeSpan idleTimeout)
    {
        var idleSince = Volatile.Read(ref _idleSinceTicks);
        return idleSince != 0
            && Volatile.Read(ref _leaseCount) == 0
            && now.UtcTicks - idleSince >= idleTimeout.Ticks;
    }

    /// <summary>
    /// Whether this association may be retired because it faulted with no lease outstanding. Its
    /// last release already ran while the association still looked healthy (or private), so no
    /// holder is left to close it and it would otherwise linger in the pool's live set until
    /// shutdown. Only the maintenance sweep calls this — never the watchdog, which must not dispose
    /// the association from inside one of its children.
    /// </summary>
    internal bool CanRetireFaulted => IsFaulted && Volatile.Read(ref _leaseCount) == 0;

    private Task StartAssociateAsync(CancellationToken cancellationToken)
    {
        lock (_associateGate)
        {
            return _associateTask ??= AssociateCoreAsync(cancellationToken);
        }
    }

    private async Task AssociateCoreAsync(CancellationToken cancellationToken)
    {
        Socks5ControlConnection? control = null;
        try
        {
            control = await DialAsync(cancellationToken).ConfigureAwait(false);
            var relay = await control.UdpAssociateAsync(cancellationToken).ConfigureAwait(false);
            PublishRelay(relay);
            Volatile.Write(ref _control, control);
            control = null;
            StartWatchdog();
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (control is not null) await control.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void StartWatchdog()
    {
        if (Interlocked.Exchange(ref _watchStarted, 1) != 0) return;
        // A sealed scope refuses the child; the pool is tearing the association down, which closes
        // the control connection this watchdog would have watched.
        _scope.Run(WatchAsync, "udp.association.watch");
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

    private void PublishRelay(IPEndPoint relay) =>
        Volatile.Write(ref _relayTarget, new UdpRelayTarget(relay, relay.Serialize()));

    /// <summary>
    /// The watchdog: blocks on the control stream and treats a server-side close (a 0-byte read)
    /// or a stream fault as association death. RFC 1928 defines no control-connection traffic after
    /// UDP ASSOCIATE, so any received byte is not death and the read continues. Our own
    /// cancellation and disposal are a normal exit — never a fault, never a recovery.
    /// <para>
    /// Any other fault is association death too: nothing else observes this child, so an escaping
    /// exception would leave the association placed and every attached flow sending to a relay no
    /// one is watching. Faulting here is the watchdog's designed exit (<see cref="FaultAsync"/> is
    /// safe from this child, and it never disposes the association, which would self-drain).
    /// </para>
    /// </summary>
    private async Task WatchAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && !_scope.IsSealed)
            {
                var control = Volatile.Read(ref _control);
                if (control is null) return;
                var death = await AwaitControlDeathAsync(control, token).ConfigureAwait(false);
                if (death is null || token.IsCancellationRequested || _scope.IsSealed || IsFaulted) return;
                if (!await TryRecoverAsync(control, death, token).ConfigureAwait(false)) return;
            }
        }
        catch (Exception exception) when (!IsFaulted && (exception is not OperationCanceledException || !token.IsCancellationRequested))
        {
            await FaultAsync(
                new IOException("The SOCKS5 UDP association watchdog faulted; the association is no longer watched.", exception),
                _context.Logger).ConfigureAwait(false);
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
    /// Re-dials and re-ASSOCIATEs once, inside a bounded budget. When the new relay shares the old
    /// address family the new endpoint is published and every attached flow simply keeps sending to
    /// it; a family change or a failed recovery faults the association, which each attached
    /// transport surfaces as <c>UdpAssociationLostException</c> on its next send (I4/I7).
    /// </summary>
    private async ValueTask<bool> TryRecoverAsync(Socks5ControlConnection dead, Exception death, CancellationToken token)
    {
        _owner.OnAssociationRecovering(this);
        try
        {
            await dead.DisposeAsync().ConfigureAwait(false);
            using var recovery = CancellationTokenSource.CreateLinkedTokenSource(token);
            recovery.CancelAfter(_context.RecoveryTimeout);
            Socks5ControlConnection? replacement = null;
            try
            {
                replacement = await DialAsync(recovery.Token).ConfigureAwait(false);
                var relay = await replacement.UdpAssociateAsync(recovery.Token).ConfigureAwait(false);
                var previousFamily = RelayAddressFamily;
                if (relay.AddressFamily != previousFamily)
                {
                    await FaultAsync(
                        new IOException(
                            $"The re-associated UDP relay changed address family ({previousFamily} -> {relay.AddressFamily}); attached flows keep their relay sockets and cannot follow.",
                            death),
                        _context.Logger).ConfigureAwait(false);
                    return false;
                }

                PublishRelay(relay);
                Volatile.Write(ref _control, replacement);
                replacement = null;
                RuntimeCounters.Shared.Increment(RuntimeCounters.UdpAssociationRecovered);
                if (_context.Logger.IsEnabled(RuntimeLogLevel.Debug)) _context.Logger.Event(RuntimeLogLevel.Debug, "udp.association.recovered", new("proxy", Server.Name), new("relay", relay), new("flows", LeaseCount));
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception failure)
            {
                await FaultAsync(
                    new IOException("The SOCKS5 UDP association could not be re-established after its control connection ended.", new AggregateException(death, failure)),
                    _context.Logger).ConfigureAwait(false);
                return false;
            }
            finally
            {
                if (replacement is not null) await replacement.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _owner.OnAssociationRecovered(this);
        }
    }

    private async ValueTask FaultAsync(Exception fault, IRuntimeLogger logger)
    {
        Interlocked.CompareExchange(ref _fault, fault, comparand: null);
        _owner.OnAssociationFaulted(this, fault, logger);
        var control = Interlocked.Exchange(ref _control, value: null);
        if (control is not null) await control.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref _disposeStarted, 1) != 0
            ? new ValueTask(_scope.DrainAsync())
            : new ValueTask(DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        _owner.OnAssociationClosed(this);
        // Sealing happens synchronously here, before the control connection is disposed, so the
        // watchdog can never mistake our own teardown for association death and start a recovery.
        var drain = _scope.DrainAsync();
        try
        {
            var control = Interlocked.Exchange(ref _control, value: null);
            if (control is not null) await control.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await drain.ConfigureAwait(false);
            // The pool gate now refuses new attachments, so the live set is emptied rather than left
            // holding evidence a late reader could mistake for an attached lease.
            lock (_evidenceGate) _liveEvidence.Clear();
        }
    }
}
