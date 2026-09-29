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
/// runtime: associations never mix servers, and each server's set is bounded by the configured
/// association ceiling (<see cref="MaxAssociationsPerServerLimit"/>, <c>udpAssociationMaxPerServer</c>,
/// 1,024 by default) shared associations of up to <see cref="FlowsPerAssociationLimit"/> concurrent
/// flows each (<c>udpAssociationFlowsPerAssociation</c>, 16 by default). The two multiply into the
/// shared head — 16,384 flows per server at the defaults, which is the default
/// <c>udpSessionCapacity</c>, so every flow the coordinator admits can be shared. The coordinator's
/// capacity, not the head, bounds the population.
/// <para>
/// Placement picks the least-loaded shared association (creation order breaks ties); once every
/// shared association is full and the ceiling is reached, the flow is served from a private
/// association instead of being refused — the pool never refuses a flow. The ceiling is a bound on
/// connections, not a preallocation: a lightly loaded server holds only the associations its flows
/// need. The least-loaded scan is O(shared associations) at most — 1,024 at the default ceiling —
/// and runs once per flow setup, never on the datagram path.
/// </para>
/// <para>
/// <see cref="UdpAssociationReuseMode.Off"/> creates one private association per lease, which is
/// today's per-flow behaviour byte for byte. <see cref="UdpAssociationReuseMode.Always"/> shares
/// unconditionally, with capability detection disabled.
/// <see cref="UdpAssociationReuseMode.Auto"/> (the production default) shares while the server's
/// <see cref="UdpServerCapability"/> is <see cref="UdpServerCapability.Unknown"/> or
/// <see cref="UdpServerCapability.SharedOk"/>, and stops placing flows on shared associations for a
/// server once the sampler detects source-port pinning. The verdict is sticky for the run and never
/// tears a session down: the associations already placed keep serving their attached flows and
/// drain through normal release and retention.
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
    /// <summary>
    /// The default ceiling on shared associations per server (I6): a ceiling, not a preallocation —
    /// the pool opens only the associations placement needs — and exceeding it falls back to
    /// per-flow associations for that server instead of refusing a flow. 1,024 multiplied by
    /// <see cref="DefaultFlowsPerAssociation"/> covers the default configuration's 16,384 concurrent
    /// UDP sessions, so every flow the coordinator admits can be shared.
    /// </summary>
    internal const int DefaultMaxAssociationsPerServer = ConfigurationLoader.DefaultUdpAssociationMaxPerServer;

    /// <summary>
    /// The default number of concurrent flows one shared association serves (I6), which is also the
    /// blast radius of an association death and the capability sampler's live-evidence set.
    /// Deliberately the small knob: the per-server ceiling above carries the shared head.
    /// </summary>
    internal const int DefaultFlowsPerAssociation = ConfigurationLoader.DefaultUdpAssociationFlowsPerAssociation;

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
    /// <param name="maxAssociationsPerServer">The validated <c>udpAssociationMaxPerServer</c> ceiling; <see cref="DefaultMaxAssociationsPerServer"/> when the composition supplies none.</param>
    /// <param name="flowsPerAssociation">The validated <c>udpAssociationFlowsPerAssociation</c> bound; <see cref="DefaultFlowsPerAssociation"/> when the composition supplies none.</param>
    internal UdpAssociationPool(
        SelfTrafficRegistry selfTraffic,
        UdpAssociationReuseMode mode,
        Socks5AddressCache? addressCache = null,
        TimeProvider? timeProvider = null,
        IRuntimeLogger? logger = null,
        Func<Socks5Server, CancellationToken, ValueTask<Socks5ControlConnection>>? createControl = null,
        TimeSpan? recoveryTimeout = null,
        int maxAssociationsPerServer = DefaultMaxAssociationsPerServer,
        int flowsPerAssociation = DefaultFlowsPerAssociation)
    {
        ArgumentNullException.ThrowIfNull(selfTraffic);
        var recovery = recoveryTimeout ?? UdpControlAssociation.s_defaultRecoveryTimeout;
        if (recovery <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(recoveryTimeout), recovery, "The recovery timeout must be positive.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAssociationsPerServer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flowsPerAssociation);
        Context = new UdpAssociationContext(selfTraffic, addressCache, createControl, timeProvider ?? TimeProvider.System, logger ?? NullRuntimeLogger.Instance, recovery);
        Mode = mode;
        MaxAssociationsPerServerLimit = maxAssociationsPerServer;
        FlowsPerAssociationLimit = flowsPerAssociation;
        _scope = new QuiescenceScope();
        _scope.Run(MaintainAsync, "udp.association.maintain");
    }

    internal UdpAssociationContext Context { get; }

    /// <summary>
    /// The ceiling this pool places against: how many shared associations one server may hold
    /// before further flows fall back to private associations.
    /// </summary>
    internal int MaxAssociationsPerServerLimit { get; }

    /// <summary>
    /// The concurrent-flow bound this pool places against: one shared association serves at most
    /// this many flows, and it bounds how many leases the capability sampler can observe at once.
    /// </summary>
    internal int FlowsPerAssociationLimit { get; }

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
    /// The server's sticky capability verdict, or <see cref="UdpServerCapability.Unknown"/> for a
    /// server this pool has never placed a flow on. Test/diagnostic seam for the sampler.
    /// </summary>
    internal UdpServerCapability CapabilityOf(Socks5Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        lock (_gate) return _servers.TryGetValue(server, out var set) ? set.Capability : UdpServerCapability.Unknown;
    }

    /// <summary>
    /// Borrows one association for a flow: a shared association with room under
    /// <see cref="FlowsPerAssociationLimit"/>, a newly dialed shared association while the per-server
    /// ceiling allows it, or a private association otherwise. The returned lease is released exactly
    /// once by its transport and reports the association's current relay endpoint, family, and fault
    /// state for its whole lifetime.
    /// </summary>
    internal async ValueTask<UdpAssociationLease> RentAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        var admitted = _scope.TryEnter(out var poolLease);
        ObjectDisposedException.ThrowIf(!admitted, this);
        UdpControlAssociation? association = null;
        // Created before placement so the failed-setup path can hand the same record back to the
        // association it was attached to.
        var evidence = new UdpAssociationEvidence();
        try
        {
            association = Acquire(server, evidence);
            await association.EnsureAssociatedAsync(cancellationToken).ConfigureAwait(false);
            // Acquire claimed the refcount and attached the evidence while it held the gate, so a
            // concurrent sampling tick can never observe this flow without its evidence.
            return new UdpAssociationLease(association, evidence, poolLease);
        }
        catch
        {
            // The pool-scope lease is released through the finally: a failed release must not leave
            // it outstanding, because the pool's drain joins it and would then wait forever.
            try
            {
                if (association is not null) await association.ReleaseLeaseAsync(evidence).ConfigureAwait(false);
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
                    _ = SampleServerCapabilities();
                    await SweepIdleAssociationsAsync(Context.TimeProvider.GetUtcNow()).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // Retention and sampling are best-effort housekeeping: one failed tick must not kill the loop.
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
    /// while the per-server ceiling allows it, else a private one. Private associations are tracked
    /// for counting and disposal but never selected, so a flow can always be served. The scan is
    /// O(shared associations) — bounded by the configured ceiling — and runs once per flow setup.
    /// </summary>
    private UdpControlAssociation Acquire(Socks5Server server, UdpAssociationEvidence evidence)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_scope.IsSealed, this);
            if (!_servers.TryGetValue(server, out var set))
            {
                set = new ServerAssociations { Capability = InitialCapability(Mode) };
                _servers.Add(server, set);
            }

            if (SharesFor(Mode, set.Capability))
            {
                var best = LeastLoadedShared(set);
                if (best is not null) return Attach(best, evidence);
                if (set.Shared.Count < MaxAssociationsPerServerLimit) return Attach(Create(server, set, isPrivate: false), evidence);
            }

            return Attach(Create(server, set, isPrivate: true), evidence);
        }
    }

    /// <summary>
    /// The verdict a server starts with (design §5): <c>auto</c> opens on trial, <c>always</c> is a
    /// forced positive with detection disabled, and <c>off</c> is the per-flow rollback mode.
    /// </summary>
    private static UdpServerCapability InitialCapability(UdpAssociationReuseMode mode) => mode switch
    {
        UdpAssociationReuseMode.Always => UdpServerCapability.SharedOk,
        UdpAssociationReuseMode.Off => UdpServerCapability.PerFlowOnly,
        _ => UdpServerCapability.Unknown,
    };

    /// <summary>
    /// Whether a flow may join a shared association (design §5, I8): <c>always</c> shares
    /// unconditionally, <c>auto</c> shares until the sampler proves the server pins source ports,
    /// and <c>off</c> never shares. A sticky <see cref="UdpServerCapability.PerFlowOnly"/> flips
    /// only future placements — the associations already placed keep serving their attached flows.
    /// </summary>
    private static bool SharesFor(UdpAssociationReuseMode mode, UdpServerCapability capability) => mode switch
    {
        UdpAssociationReuseMode.Always => true,
        UdpAssociationReuseMode.Auto => capability != UdpServerCapability.PerFlowOnly,
        _ => false,
    };

    /// <summary>
    /// Samples every server still on trial and applies the sticky verdict (design §5, R2). Runs
    /// outside the gate for the evidence walk and re-takes it only to record a verdict, so a slow
    /// sample never blocks a rent. A server that is already settled is skipped: detection is
    /// disabled for <c>always</c> (forced <see cref="UdpServerCapability.SharedOk"/>) and never
    /// restarts for a flipped <c>auto</c> server (I8).
    /// <para>
    /// Returns 1 when this sample flipped a server and 0 otherwise. Internal so capability tests can
    /// drive the rule deterministically instead of waiting for the 5 s maintenance tick; the
    /// maintenance child calls it on its tick.
    /// </para>
    /// </summary>
    internal int SampleServerCapabilities()
    {
        List<ServerSample> candidates;
        lock (_gate)
        {
            if (_scope.IsSealed) return 0;
            candidates = [.. _servers
                .Where(static pair => pair.Value.Capability == UdpServerCapability.Unknown)
                .Select(static pair => new ServerSample(pair.Key, [.. pair.Value.Shared]))];
        }

        foreach (var candidate in candidates)
        {
            foreach (var association in candidate.Associations)
            {
                if (association.IsPrivate || association.IsFaulted) continue;
                var sample = association.SnapshotEvidence();
                // Both settling verdicts are terminal for this sample. The flip's verdict is recorded
                // under the gate before it returns, so a sibling association can never flip the same
                // server again — the counter, the warn, and the log each fire exactly once per run.
                int? settled = UdpAssociationCapabilitySampler.Evaluate(sample.Evidence, sample.Attached) switch
                {
                    UdpServerCapability.SharedOk when ConfirmServerSharing(candidate.Server) => 0,
                    UdpServerCapability.PerFlowOnly when MarkServerPerFlowOnly(candidate.Server, association, sample) => 1,
                    _ => null,
                };
                if (settled is { } result) return result;
            }
        }

        return 0;
    }

    /// <summary>
    /// Settles a server as confirmed-shareable: detection stops for it, because "a second attached
    /// flow got a response" can only be contradicted by a later sample on an association that no
    /// longer carries the evidence that produced it.
    /// </summary>
    private bool ConfirmServerSharing(Socks5Server server)
    {
        lock (_gate)
        {
            if (!_servers.TryGetValue(server, out var set)) return false;
            if (set.Capability != UdpServerCapability.Unknown) return false;
            set.Capability = UdpServerCapability.SharedOk;
        }

        return true;
    }

    /// <summary>
    /// Takes one server out of shared placement for the rest of the run and reports it once with the
    /// evidence that triggered it. The verdict is recorded under the gate before the log, so a burst
    /// of further samples (or of sibling associations) can never re-emit the fallback for the server.
    /// </summary>
    private bool MarkServerPerFlowOnly(Socks5Server server, UdpControlAssociation association, (UdpAssociationEvidence[] Evidence, int Attached) sample)
    {
        var unanswered = 0;
        var sent = 0;
        for (var index = 0; index < sample.Attached; index++)
        {
            var lease = sample.Evidence[index];
            if (lease.SawResponse) continue;
            if (lease.DatagramsSent > sent) sent = lease.DatagramsSent;
            if (lease.DatagramsSent >= UdpAssociationCapabilitySampler.PinningSuspicionThreshold) unanswered++;
        }

        lock (_gate)
        {
            if (!_servers.TryGetValue(server, out var set)) return false;
            // An association that recovered in place, faulted, or was closed while this sample ran
            // no longer describes the server: leave the server on trial for the next tick.
            if (association.IsFaulted || association.IsPrivate || !set.Shared.Contains(association)) return false;
            if (set.Capability != UdpServerCapability.Unknown) return false;
            set.Capability = UdpServerCapability.PerFlowOnly;
        }

        RuntimeCounters.Shared.Increment(RuntimeCounters.UdpAssociationFallbacks);
        // The gate-guarded sticky verdict above makes this the server's one fallback for the run, so
        // no throttle is needed: a pool-wide one would swallow the warn of a second server that flips
        // inside the same window while its counter still moved.
        if (Context.Logger.IsEnabled(RuntimeLogLevel.Warn))
        {
            Context.Logger.Event(RuntimeLogLevel.Warn, "udp.association.fallback",
                new("proxy", server.Name),
                new("reason", "source-port-pinned"),
                new("flows", sample.Attached),
                new("sent", sent),
                new("unanswered", unanswered),
                new("relay", association.RelayEndpoint));
        }

        return true;
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

    /// <summary>
    /// Attaches the flow's evidence and claims its lease on the placed association — both while the
    /// caller holds the gate, so placement and the sampling tick observe one atomic step.
    /// </summary>
    private static UdpControlAssociation Attach(UdpControlAssociation association, UdpAssociationEvidence evidence)
    {
        association.StartLease(evidence);
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

    /// <summary>One server still on trial plus a gate-safe copy of its shared associations.</summary>
    private readonly record struct ServerSample(Socks5Server Server, List<UdpControlAssociation> Associations);

    private sealed class ServerAssociations
    {
        /// <summary>Every live association of the server, in creation order (shared and private).</summary>
        public List<UdpControlAssociation> All { get; } = [];

        /// <summary>The subset eligible for sharing, in creation order (placement tie-break).</summary>
        public List<UdpControlAssociation> Shared { get; } = [];

        /// <summary>
        /// The sticky per-server capability verdict (I8). <see cref="UdpServerCapability.Unknown"/>
        /// for an <c>auto</c> server that is still on trial; <c>always</c> forces
        /// <see cref="UdpServerCapability.SharedOk"/> and <c>off</c> forces
        /// <see cref="UdpServerCapability.PerFlowOnly"/> at creation, so detection is disabled for
        /// both and a pinned <c>always</c> server can never flip.
        /// </summary>
        public UdpServerCapability Capability { get; set; } = UdpServerCapability.Unknown;
    }
}
