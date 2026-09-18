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

    /// <summary>A flow was blocked because the flow table is at capacity; see <c>flow.capacity-block</c>.</summary>
    public const string FlowCapacityBlock = "flowCapacityBlock";

    /// <summary>Host-flow process attribution returned no owner (after the attributor's internal retry); see <c>flow.attribution-miss</c>.</summary>
    public const string AttributionMiss = "attributionMiss";

    /// <summary>A pass-through reinjection native send failed; see <c>reinject.pass-failed</c>.</summary>
    public const string PassReinjectFailed = "passReinjectFailed";

    /// <summary>The process-wide aggregate wired into production call sites; tests use private instances for isolation.</summary>
    public static RuntimeCounters Shared { get; } = new();

    private readonly ConcurrentDictionary<string, StrongBox<long>> _counters = new(StringComparer.Ordinal);

    /// <summary>Atomically increments <paramref name="key"/> and returns the new value; the counter box is created on first hit.</summary>
    public long Increment(string key)
    {
        var counter = GetOrAddCounter(key);
        return Interlocked.Increment(ref counter.Value);
    }

    /// <summary>Atomically adds <paramref name="value"/> to <paramref name="key"/> and returns the new value.</summary>
    public long Add(string key, long value)
    {
        var counter = GetOrAddCounter(key);
        return Interlocked.Add(ref counter.Value, value);
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

    private StrongBox<long> GetOrAddCounter(string key) =>
        _counters.GetOrAdd(key, static _ => new StrongBox<long>());
}
