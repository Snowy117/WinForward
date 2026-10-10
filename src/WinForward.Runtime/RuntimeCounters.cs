using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace WinForward.Runtime;

/// <summary>
/// Process-wide named long counters for runtime diagnostics (failure/decision aggregates that the
/// periodic heartbeat summarizes and the interception-health monitor may threshold on). A thin
/// interlocked-counter map: values live in mutable boxes so an increment is a single
/// <see cref="Interlocked.Increment(ref long)"/> with zero allocation once a key exists (the box is
/// created once per key); reads take lock-free snapshots. Counters are observational only — they
/// must never influence packet disposition, fail-closed, relay, or shutdown decisions.
/// </summary>
public sealed class RuntimeCounters
{
    /// <summary>A TCP redirect relay setup failed (dial/auth/CONNECT); see <c>tcp.redirect.relaySetupFailed</c>.</summary>
    public const string RelaySetupFailed = "relaySetupFailed";

    /// <summary>A host UDP response could not resolve its origin adapter and fell back to the host target; see <c>udp.reinject.unresolved</c>.</summary>
    public const string UdpOriginUnresolved = "udpOriginUnresolved";

    /// <summary>A UDP response was dropped fail-closed (unresolvable origin/host target); see <c>udp.reinject.drop</c>.</summary>
    public const string UdpFailClosedDrop = "udpFailClosedDrop";

    /// <summary>A UDP relay response declared a source other than the receiving flow's own destination (a server may legitimately answer from another endpoint); see <c>udp.response.foreign_source</c>.</summary>
    public const string UdpResponseSourceMismatch = "udpResponseSourceMismatch";

    /// <summary>A flow was blocked because the flow table is at capacity; see <c>flow.capacity-block</c>.</summary>
    public const string FlowCapacityBlock = "flowCapacityBlock";

    /// <summary>Host-flow process attribution returned no owner (after the attributor's internal retry); see <c>flow.attribution-miss</c>.</summary>
    public const string AttributionMiss = "attributionMiss";

    /// <summary>A successful system-wide owner-table scan for process attribution — runs once per flow whose lookup no snapshot could answer, so it tracks the new-flow rate, not the packet rate.</summary>
    public const string AttributionOwnerTableScans = "attributionOwnerTableScans";

    /// <summary>A new-flow packet was refused a pending-attribution entry (entry cap or global retained-byte budget); see the rate-limited <c>flow.attribution.pending-rejected</c> warn.</summary>
    public const string AttributionPendingRejected = "attributionPendingRejected";

    /// <summary>A packet of an already-pending flow was refused (per-flow ring full, or a frame larger than the retention pool's buffer); see the rate-limited <c>flow.attribution.flow-full</c> trace.</summary>
    public const string AttributionFlowFull = "attributionFlowFull";

    /// <summary>A packet was blocked fail-closed because the attribution pipeline is sealed (shutdown); the flow is never attributed inline instead.</summary>
    public const string AttributionSealed = "attributionSealed";

    /// <summary>A packet was blocked fail-closed because the flow is inside its post-failure attribution cooldown.</summary>
    public const string AttributionCooldownBlocks = "attributionCooldownBlocks";

    /// <summary>The setup executor refused the attribution work item (ring full); the entry was failed closed rather than left pending.</summary>
    public const string AttributionSetupRejected = "attributionSetupRejected";

    /// <summary>A genuine attribution or policy failure failed one flow's pending packets closed and arms the 1 s per-flow cooldown. Shutdown cancellation is deliberately not counted here.</summary>
    public const string AttributionSetupFailed = "attributionSetupFailed";

    /// <summary>The flow-table claim failed at capacity after the pending packets were delivered; the entry is kept and its later packets block fail-closed.</summary>
    public const string AttributionClaimFailed = "attributionClaimFailed";

    /// <summary>A pending attribution entry was reclaimed at its retention TTL; its packets were blocked and a later packet of the flow re-admits.</summary>
    public const string AttributionPendingTtlExpired = "attributionPendingTtlExpired";

    /// <summary>A transport tuple already attributed (on another adapter, or after its entry was reclaimed) admitted a second entry and paid a second attribution.</summary>
    public const string AttributionReAdmission = "attributionReAdmission";

    /// <summary>A pass-through reinjection native send failed; see <c>reinject.pass-failed</c>.</summary>
    public const string PassReinjectFailed = "passReinjectFailed";

    /// <summary>A UDP datagram was rejected because the session budget was full; see the rate-limited <c>udp.session.capacity-block</c> warn and the per-datagram <c>udp.session.rejected</c> trace.</summary>
    public const string UdpCapacityRejections = "udpCapacityRejections";

    /// <summary>A UDP flow was refused because the setup ring had no free work item; see <c>udp.session.rejected</c>.</summary>
    public const string UdpSetupRejections = "udpSetupRejections";

    /// <summary>A UDP session setup failed against the SOCKS5 server (a genuine failure, not shutdown cancellation); see <c>udp.setup.failed</c>.</summary>
    public const string UdpSetupFailures = "udpSetupFailures";

    /// <summary>A UDP setup datagram was rejected by the aggregate setup-queue byte budget; see <c>udp.setupqueue.dropped</c>.</summary>
    public const string UdpSetupBudgetRejections = "udpSetupBudgetRejections";

    /// <summary>A flow's SOCKS5 UDP association died (its control stream ended), so its slot was removed as <c>UdpTeardownReason.AssociationLost</c>; see the rate-limited <c>udp.association.lost</c> warn.</summary>
    public const string UdpAssociationLost = "udpAssociationLost";

    /// <summary>A UDP flow was created over a local target instead of a SOCKS5 association; the trace log names the flow's target on <c>udp.session.created</c>.</summary>
    public const string UdpLocalTargetFlows = "udpLocalTargetFlows";

    /// <summary>
    /// A local-target transport could not hand a datagram to its socket (an oversize payload, or a send
    /// the socket refused) or its receive faulted fatally; the flow fails closed and re-establishes on its
    /// next datagram. Teardown is not counted: a cancelled receive or send (idle expiry, shutdown) and a
    /// receive that ends because the socket was closed are the normal end of a session, not failures.
    /// </summary>
    public const string UdpLocalTargetFailures = "udpLocalTargetFailures";

    /// <summary>
    /// The native-pool diagnostic key prefix: every registered pool records cumulative rents and
    /// returns under <c>pool.&lt;name&gt;.rented</c> / <c>pool.&lt;name&gt;.returned</c>, which the heartbeat
    /// surfaces as per-key deltas plus the aggregate occupancy (rented − returned).
    /// </summary>
    public const string PoolCounterPrefix = "pool.";

    /// <summary>The process-wide aggregate wired into production call sites; tests use private instances for isolation.</summary>
    public static RuntimeCounters Shared { get; } = new();

    private readonly ConcurrentDictionary<string, StrongBox<long>> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pools = new(StringComparer.Ordinal);

    /// <summary>Atomically increments <paramref name="key"/> and returns the new value; the counter box is created on first hit.</summary>
    public long Increment(string key)
    {
        var counter = GetOrAddCounter(key);
        return Interlocked.Increment(ref counter.Value);
    }

    /// <summary>Atomically adds <paramref name="value"/> to <paramref name="key"/>.</summary>
    public void Add(string key, long value)
    {
        var counter = GetOrAddCounter(key);
        _ = Interlocked.Add(ref counter.Value, value);
    }

    /// <summary>The current value of <paramref name="key"/>, or 0 when the key was never touched.</summary>
    public long Get(string key) => _counters.TryGetValue(key, out var counter) ? Interlocked.Read(ref counter.Value) : 0;

    /// <summary>A lock-free point-in-time copy of every counter (the heartbeat's aggregate source).</summary>
    public IReadOnlyDictionary<string, long> Snapshot()
    {
        var snapshot = new Dictionary<string, long>(_counters.Count, StringComparer.Ordinal);
        foreach (var pair in _counters) snapshot[pair.Key] = Interlocked.Read(ref pair.Value.Value);
        return snapshot;
    }

    /// <summary>The cumulative-rents key for a registered pool; see <see cref="PoolCounterPrefix"/>.</summary>
    public static string PoolRentedKey(string poolName) => $"{PoolCounterPrefix}{poolName}.rented";

    /// <summary>The cumulative-returns key for a registered pool; see <see cref="PoolCounterPrefix"/>.</summary>
    public static string PoolReturnedKey(string poolName) => $"{PoolCounterPrefix}{poolName}.returned";

    /// <summary>
    /// The registered pool names in ordinal order (snapshot copy). Pools register once at
    /// creation; rent/return activity also implies registration, so a pool with recorded
    /// activity is never missing from the aggregate occupancy.
    /// </summary>
    public IReadOnlyList<string> GetRegisteredPools() =>
        [.. _pools.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Registers a native buffer pool under <paramref name="poolName"/> (idempotent) and pre-creates
    /// its counters so occupancy reads are race-free.
    /// </summary>
    public void RegisterPool(string poolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poolName);
        _ = _pools.TryAdd(poolName, 0);
        _ = GetOrAddCounter(PoolRentedKey(poolName));
        _ = GetOrAddCounter(PoolReturnedKey(poolName));
    }

    /// <summary>Records one buffer rent for the pool (cumulative counter increment).</summary>
    internal void RecordPoolRent(string poolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poolName);
        _ = _pools.TryAdd(poolName, 0);
        _ = Increment(PoolRentedKey(poolName));
    }

    /// <summary>Records one buffer return for the pool (cumulative counter increment).</summary>
    internal void RecordPoolReturn(string poolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poolName);
        _ = _pools.TryAdd(poolName, 0);
        _ = Increment(PoolReturnedKey(poolName));
    }

    /// <summary>
    /// Buffers currently held by consumers of the pool: cumulative rents minus returns. The
    /// value is reported honestly (a negative result indicates double-reported returns, which
    /// the aggregate is meant to make visible); pools never influence behavior.
    /// </summary>
    public long GetPoolOccupancy(string poolName) => Get(PoolRentedKey(poolName)) - Get(PoolReturnedKey(poolName));

    /// <summary>The sum of every registered pool's occupancy (the heartbeat's leak-watch aggregate).</summary>
    public long GetTotalPoolOccupancy()
    {
        long total = 0;
        foreach (var name in _pools.Keys)
        {
            total += GetPoolOccupancy(name);
        }
        return total;
    }

    private StrongBox<long> GetOrAddCounter(string key) =>
        _counters.GetOrAdd(key, static _ => new StrongBox<long>());
}
