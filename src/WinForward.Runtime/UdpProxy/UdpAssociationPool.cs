using System.Runtime.ExceptionServices;
using WinForward.Configuration;
using WinForward.Runtime.Socks5;

namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// The collaborators every association of one pool is built from: the loop-prevention registry the
/// control tuple is registered in, the shared SOCKS5 address cache, the dial seam, the clock, the
/// logger, and the re-association budget. A value type — the pool holds one and copies it into each
/// association.
/// </summary>
internal readonly record struct UdpAssociationContext(
    SelfTrafficRegistry SelfTraffic,
    Socks5AddressCache? AddressCache,
    Func<Socks5Server, CancellationToken, ValueTask<Socks5ControlConnection>>? CreateControl,
    TimeProvider TimeProvider,
    IRuntimeLogger Logger,
    TimeSpan RecoveryTimeout);

/// <summary>
/// The per-server warm set of authenticated SOCKS5 UDP associations. One pool serves the whole
/// runtime: associations never mix servers, and each server's set is bounded by
/// <see cref="MaxAssociationsPerServer"/> shared associations of up to
/// <see cref="FlowsPerAssociation"/> concurrent flows. Placement picks the least-loaded shared
/// association (creation order breaks ties); once every shared association is full and the cap is
/// reached, the flow is served from a private association instead of being refused — the pool never
/// refuses a flow.
/// <para>
/// <see cref="UdpAssociationReuseMode.Off"/> creates one private association per lease, which is
/// today's per-flow behaviour byte for byte. <see cref="UdpAssociationReuseMode.Auto"/> is
/// off-equivalent until Step 3 adds passive capability detection.
/// </para>
/// <para>
/// Ownership: the pool owns one <see cref="QuiescenceScope"/> (its lifetime token, the maintenance
/// child, and one lease per outstanding flow lease), D11 one-shot teardown, and the nested drain —
/// <see cref="DisposeAsync"/> seals, joins every lease holder, and only then closes every
/// association, so no lease can outlive the pool.
/// </para>
/// </summary>
internal sealed class UdpAssociationPool : IAsyncDisposable
{
    /// <summary>The bound on shared associations per server (I6); beyond it flows fall back to private associations.</summary>
    internal const int MaxAssociationsPerServer = 16;

    /// <summary>The bound on concurrent flows one shared association serves (I6), which is also the blast radius of an association death.</summary>
    internal const int FlowsPerAssociation = 16;

    /// <summary>How long an association with no outstanding lease is kept warm for reuse.</summary>
    internal static readonly TimeSpan s_idleRetireTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan s_maintenanceInterval = TimeSpan.FromSeconds(5);

    private readonly QuiescenceScope _scope;
    private readonly Lock _gate = new();
    private readonly Dictionary<Socks5Server, ServerAssociations> _servers = [];
    private readonly RuntimeLogThrottle _faultLog = new(TimeSpan.FromSeconds(5));
    private int _disposeStarted;

    /// <summary>Creates the pool and starts its warm-retention tick.</summary>
    /// <param name="selfTraffic">Loop-prevention registry the control tuple is registered in before the SYN leaves the host.</param>
    /// <param name="mode">The validated <c>udpAssociationReuse</c> mode.</param>
    /// <param name="addressCache">Shared SOCKS5 endpoint cache; null resolves on every dial.</param>
    /// <param name="timeProvider">Clock driving the warm-retention window (injectable for fake-time tests).</param>
    /// <param name="logger">Lifecycle diagnostics; null keeps the pool silent.</param>
    /// <param name="createControl">Test seam replacing the real dial, mirroring the former transport parameter.</param>
    /// <param name="recoveryTimeout">Test seam bounding one in-place re-association attempt; null uses <see cref="UdpControlAssociation.s_defaultRecoveryTimeout"/>.</param>
    internal UdpAssociationPool(
        SelfTrafficRegistry selfTraffic,
        UdpAssociationReuseMode mode,
        Socks5AddressCache? addressCache = null,
        TimeProvider? timeProvider = null,
        IRuntimeLogger? logger = null,
        Func<Socks5Server, CancellationToken, ValueTask<Socks5ControlConnection>>? createControl = null,
        TimeSpan? recoveryTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(selfTraffic);
        var recovery = recoveryTimeout ?? UdpControlAssociation.s_defaultRecoveryTimeout;
        if (recovery <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(recoveryTimeout), recovery, "The recovery timeout must be positive.");
        Context = new UdpAssociationContext(selfTraffic, addressCache, createControl, timeProvider ?? TimeProvider.System, logger ?? NullRuntimeLogger.Instance, recovery);
        Mode = mode;
        _scope = new QuiescenceScope();
        _scope.Run(MaintainAsync, "udp.association.maintain");
    }

    internal UdpAssociationContext Context { get; }

    /// <summary>The validated reuse mode this pool was created with.</summary>
    private UdpAssociationReuseMode Mode { get; }

    internal CancellationToken Token => _scope.Token;

    /// <summary>The number of live associations across every server (shared and private); diagnostics and tests.</summary>
    internal int AssociationCount
    {
        get
        {
            lock (_gate) return _servers.Values.Sum(static set => set.All.Count);
        }
    }

    /// <summary>The number of outstanding flow leases; diagnostics and tests.</summary>
    internal int LeasedFlowCount
    {
        get
        {
            lock (_gate) return _servers.Values.Sum(static set => set.All.Sum(static association => association.LeaseCount));
        }
    }

    /// <summary>
    /// Borrows one association for a flow: a shared association with room under
    /// <see cref="FlowsPerAssociation"/>, a newly dialed shared association while the per-server cap
    /// allows it, or a private association otherwise. The returned lease is released exactly once by
    /// its transport and reports the association's current relay endpoint, family, and fault state
    /// for its whole lifetime.
    /// </summary>
    internal async ValueTask<UdpAssociationLease> RentAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        var admitted = _scope.TryEnter(out var poolLease);
        ObjectDisposedException.ThrowIf(!admitted, this);
        UdpControlAssociation? association = null;
        try
        {
            association = Acquire(server);
            await association.EnsureAssociatedAsync(cancellationToken).ConfigureAwait(false);
            return new UdpAssociationLease(association, poolLease);
        }
        catch
        {
            // The pool-scope lease is released through the finally: a failed release must not leave
            // it outstanding, because the pool's drain joins it and would then wait forever.
            try
            {
                if (association is not null) await association.ReleaseLeaseAsync().ConfigureAwait(false);
            }
            finally
            {
                poolLease.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Closes an association that has held no lease for <see cref="s_idleRetireTimeout"/>, and one
    /// that faulted with no lease outstanding (its last release already ran while it still looked
    /// healthy, so nothing else will close it). Never closes one with an outstanding lease, and
    /// never one that is still being placed. Internal so fake-time tests drive the retention
    /// boundary deterministically; the maintenance child calls it on its tick.
    /// </summary>
    internal async ValueTask<int> SweepIdleAssociationsAsync(DateTimeOffset now)
    {
        List<UdpControlAssociation> retired;
        lock (_gate)
        {
            if (_scope.IsSealed) return 0;
            retired = [.. _servers.Values.SelectMany(static set => set.All)
                .Where(association => association.CanRetire(now, s_idleRetireTimeout) || association.CanRetireFaulted)];
            foreach (var association in retired)
            {
                if (_servers.TryGetValue(association.Server, out var set)) set.Shared.Remove(association);
            }
        }

        foreach (var association in retired) await association.DisposeAsync().ConfigureAwait(false);
        return retired.Count;
    }

    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref _disposeStarted, 1) != 0
            ? new ValueTask(_scope.DrainAsync())
            : new ValueTask(DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        // Seal and join first: the seal refuses every later rent and the join waits for every
        // outstanding flow lease, so closing the associations below cannot strand a holder.
        await _scope.DrainAsync().ConfigureAwait(false);
        UdpControlAssociation[] live;
        lock (_gate)
        {
            live = [.. _servers.Values.SelectMany(static set => set.All)];
            _servers.Clear();
        }

        // Every association is closed even when an earlier one faults its disposal.
        Exception? failure = null;
        foreach (var association in live)
        {
            try
            {
                await association.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task MaintainAsync(CancellationToken token)
    {
        // The tick runs on the system clock even when the injected one drives the retention
        // comparison: a clock injected for fake-time expiry (MutableTimeProvider) has no
        // CreateTimer, and constructing the timer from it would fault this child at entry —
        // silently killing retention for the whole run.
        using var timer = new PeriodicTimer(s_maintenanceInterval, TimeProvider.System);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await SweepIdleAssociationsAsync(Context.TimeProvider.GetUtcNow()).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // Retention is best-effort housekeeping: one failed sweep must not kill the loop.
                    Context.Logger.Warn($"UDP association retention sweep failed: {exception.GetType().Name}: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
    }

    /// <summary>
    /// Placement: the least-loaded shared association with room, else a fresh shared association
    /// while the per-server cap allows it, else a private one. Private associations are tracked for
    /// counting and disposal but never selected, so a flow can always be served.
    /// </summary>
    private UdpControlAssociation Acquire(Socks5Server server)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_scope.IsSealed, this);
            if (!_servers.TryGetValue(server, out var set))
            {
                set = new ServerAssociations();
                _servers.Add(server, set);
            }

            if (Mode == UdpAssociationReuseMode.Always)
            {
                var best = LeastLoadedShared(set);
                if (best is not null) return Attach(best);
                if (set.Shared.Count < MaxAssociationsPerServer) return Attach(Create(server, set, isPrivate: false));
            }

            return Attach(Create(server, set, isPrivate: true));
        }
    }

    private static UdpControlAssociation? LeastLoadedShared(ServerAssociations set)
    {
        UdpControlAssociation? best = null;
        foreach (var candidate in set.Shared)
        {
            if (!candidate.IsAvailableForPlacement) continue;
            // Strictly-less keeps the earliest-created association on a tie (creation order).
            if (best is null || candidate.LeaseCount < best.LeaseCount) best = candidate;
        }

        return best;
    }

    private static UdpControlAssociation Attach(UdpControlAssociation association)
    {
        association.AttachLease();
        return association;
    }

    private UdpControlAssociation Create(Socks5Server server, ServerAssociations set, bool isPrivate)
    {
        var association = new UdpControlAssociation(this, server, isPrivate);
        set.All.Add(association);
        if (!isPrivate) set.Shared.Add(association);
        return association;
    }

    /// <summary>Removes an association from placement while its recovery attempt runs (called outside the gate).</summary>
    internal void OnAssociationRecovering(UdpControlAssociation association)
    {
        lock (_gate)
        {
            if (_servers.TryGetValue(association.Server, out var set) && set.Shared.Contains(association)) association.Recovering = true;
        }
    }

    /// <summary>Re-admits an association to placement after its recovery attempt settles.</summary>
    internal void OnAssociationRecovered(UdpControlAssociation association)
    {
        lock (_gate) association.Recovering = false;
    }

    /// <summary>
    /// Takes an unrecoverable association out of placement. Its attached flows keep their leases —
    /// each fails closed on its next send — and the association is closed by its last lease release.
    /// </summary>
    internal void OnAssociationFaulted(UdpControlAssociation association, Exception fault, IRuntimeLogger logger)
    {
        lock (_gate)
        {
            if (_servers.TryGetValue(association.Server, out var set)) set.Shared.Remove(association);
        }

        if (logger.IsEnabled(RuntimeLogLevel.Warn) && _faultLog.ShouldEmit())
        {
            logger.Event(RuntimeLogLevel.Warn, "udp.association.lost",
                new("proxy", association.Server.Name),
                new("relay", association.RelayEndpoint),
                new("flows", association.LeaseCount),
                new("reason", fault.GetType().Name));
        }
    }

    /// <summary>Drops a closed association from the live set (called by <see cref="UdpControlAssociation.DisposeAsync"/>).</summary>
    internal void OnAssociationClosed(UdpControlAssociation association)
    {
        lock (_gate)
        {
            if (!_servers.TryGetValue(association.Server, out var set)) return;
            set.Shared.Remove(association);
            set.All.Remove(association);
        }
    }

    private sealed class ServerAssociations
    {
        /// <summary>Every live association of the server, in creation order (shared and private).</summary>
        public List<UdpControlAssociation> All { get; } = [];

        /// <summary>The subset eligible for sharing, in creation order (placement tie-break).</summary>
        public List<UdpControlAssociation> Shared { get; } = [];
    }
}
