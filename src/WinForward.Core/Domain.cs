using System.Net;
using System.Runtime.InteropServices;

namespace WinForward.Core;

public enum TransportProtocol
{
    Tcp,
    Udp,
}

public enum AddressFamilyKind
{
    IPv4,
    IPv6,
}

public enum FlowOriginKind
{
    Host,
    Forwarded,
}

public enum FlowAction
{
    Proxy,
    Pass,
    Block,
}

[StructLayout(LayoutKind.Auto)]
public readonly struct Endpoint : IEquatable<Endpoint>
{
    // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local // Caller-facing boundary contract: ProcessAttribution decodes raw Win32 rows and must state the family; the parameter exists to fail closed on a family/address mismatch, not to feed the value (the family is derived from the address afterwards).
    public Endpoint(AddressFamilyKind addressFamily, IPAddress address, ushort port)
        : this(IPAddressValue.From(address), port)
    {
        if (addressFamily != AddressFamily) throw new ArgumentException("Endpoint address family does not match the address.", nameof(addressFamily));
    }

    private Endpoint(IPAddressValue address, ushort port)
    {
        Address = address;
        Port = port;
    }

    public IPAddressValue Address { get; }
    public ushort Port { get; }
    public AddressFamilyKind AddressFamily => Address.Family;

    /// <summary>Cold-edge constructor from a framework address; allocates nothing but the conversion cost.</summary>
    public static Endpoint From(IPAddress address, ushort port)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new(IPAddressValue.From(address), port);
    }

    /// <summary>Hot-path constructor used by packet classification; never allocates.</summary>
    public static Endpoint From(IPAddressValue address, ushort port) => new(address, port);

    public bool Equals(Endpoint other) => Port == other.Port && Address.Equals(other.Address);
    public override bool Equals(object? obj) => obj is Endpoint other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Port, Address);

    public static bool operator ==(Endpoint left, Endpoint right) => left.Equals(right);
    public static bool operator !=(Endpoint left, Endpoint right) => !left.Equals(right);

    public override string ToString() => AddressFamily == AddressFamilyKind.IPv6
        ? $"[{Address}]:{Port}"
        : $"{Address}:{Port}";
}

public readonly record struct AdapterContext(string? StableId, long Generation);

public readonly record struct FlowKey(
    AddressFamilyKind AddressFamily,
    TransportProtocol Protocol,
    Endpoint Local,
    Endpoint Remote,
    FlowOriginKind Origin,
    string? OriginAdapterId,
    long OriginAdapterGeneration)
{
    public static FlowKey Create(Endpoint local, Endpoint remote, TransportProtocol protocol, FlowOriginKind origin, AdapterContext? adapter = null)
    {
        // ReSharper disable once ConvertIfStatementToReturnStatement // Guard-clause + throw reads failure-first; the suggested `cond ? throw ... : value` form has no precedent in this repo (B1 disposition).
        if (local.AddressFamily != remote.AddressFamily) throw new ArgumentException("Flow endpoints must use the same address family.", nameof(remote));
        return new(local.AddressFamily, protocol, local, remote, origin, adapter?.StableId, adapter?.Generation ?? 0);
    }

    /// <summary>Flat mix over the endpoint addresses and ports via <see cref="FlowHash"/>: one pass,
    /// no per-field chaining, tuned for dictionary keys probed on every packet. Deliberately omits
    /// the origin fields that <see cref="Equals(FlowKey)"/> compares: keys differing only in origin
    /// kind or origin adapter are the same logical flow seen from another orientation and must share
    /// a hash bucket — the same field set backs the flow table's orientation-agnostic transport
    /// index, whose key delegates to the same expression. An origin-aware equality over this
    /// transport-only hash can collide but never diverge, so the asymmetry is hash-consistent.</summary>
    public override int GetHashCode() => FlowHash.Combine(AddressFamily, Protocol, Local, Remote);

    /// <summary>Cheapest discriminators first; addresses last because they are the widest fields.</summary>
    public bool Equals(FlowKey other) =>
        Protocol == other.Protocol &&
        AddressFamily == other.AddressFamily &&
        Origin == other.Origin &&
        OriginAdapterGeneration == other.OriginAdapterGeneration &&
        Local.Port == other.Local.Port &&
        Remote.Port == other.Remote.Port &&
        Local.Address.Bits == other.Local.Address.Bits &&
        Remote.Address.Bits == other.Remote.Address.Bits &&
        Local.Address.Family == other.Local.Address.Family &&
        Remote.Address.Family == other.Remote.Address.Family &&
        Local.Address.ScopeId == other.Local.Address.ScopeId &&
        Remote.Address.ScopeId == other.Remote.Address.ScopeId &&
        string.Equals(OriginAdapterId, other.OriginAdapterId, StringComparison.Ordinal);

    public FlowKey Reverse() => this with { Local = Remote, Remote = Local };
}

/// <summary>
/// The one hash expression shared by <see cref="FlowKey"/> (origin-aware equality) and the flow
/// table's orientation-agnostic <c>TransportTuple</c> index key. Both GetHashCode implementations
/// delegate here so the transport-only hash set cannot drift; hashing only the transport fields lets
/// origin variants and either endpoint orientation land in shared buckets — collisions are fine,
/// divergence is not.
/// </summary>
internal static class FlowHash
{
    internal static int Combine(AddressFamilyKind addressFamily, TransportProtocol protocol, Endpoint local, Endpoint remote) => HashCode.Combine(
        (ulong)local.Address.Bits,
        (ulong)(local.Address.Bits >> 64),
        (ulong)remote.Address.Bits,
        (ulong)(remote.Address.Bits >> 64),
        local.Port,
        remote.Port,
        (byte)addressFamily,
        (byte)protocol);

    /// <summary>
    /// The order-independent form of <see cref="Combine"/>: the two endpoints are ordered by
    /// <c>(address bits, port)</c> first, so a packet and its reverse select the same value. The flow
    /// table's warm cache keys its slots on this, which is why it can serve exactly what the two
    /// orientation-aware dictionary probes serve (the table holds at most one state per transport
    /// tuple, pinned by <c>FlowTableTransportTupleIsUniqueAcrossOrigins</c>). It delegates to
    /// <see cref="Combine"/> so the cache's slot function stays the same transport-only mix the
    /// dictionaries bucket by — collisions are fine, divergence is not.
    /// </summary>
    internal static int CombineCanonical(AddressFamilyKind addressFamily, TransportProtocol protocol, Endpoint first, Endpoint second)
    {
        var (low, high) = OrdersBefore(first, second) ? (first, second) : (second, first);
        return Combine(addressFamily, protocol, low, high);
    }

    /// <summary>A total order on endpoints: address bits, then port (equal endpoints order either way).</summary>
    private static bool OrdersBefore(in Endpoint first, in Endpoint second)
    {
        var bits = first.Address.Bits.CompareTo(second.Address.Bits);
        return bits != 0 ? bits < 0 : first.Port <= second.Port;
    }
}

public readonly record struct FlowDecision(FlowAction Action, int? RuleIndex, string? ProxyServerName)
{
    public static FlowDecision Fallback(FlowAction action) => new(action, RuleIndex: null, ProxyServerName: null);
}

[StructLayout(LayoutKind.Auto)]
public readonly record struct FlowContext(
    FlowKey Key,
    string? ProcessName,
    string? ProcessPath,
    string? AdapterId,
    string? AdapterName,
    ushort RemotePort);

/// <summary>
/// The validated snapshot of a pooled <see cref="FlowState"/>: the three members a warm resolve
/// consumer reads, captured while the state's publication version was stable and corroborated against
/// the key it was looked up by. A caller never holds the pooled instance across a released gate.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct FlowStateView(FlowKey Key, FlowDecision Decision, long Generation);

public sealed class FlowState
{
    /// <summary>
    /// Publication version: even while the fields are stable, odd while <see cref="Reset"/> is
    /// rewriting them, monotone (never reset to zero, never decremented). A reader brackets its field
    /// reads with this value and rejects a view whose two reads differ or whose first read is odd.
    /// Wraparound is harmless: the reader compares for equality and checks parity, neither of which a
    /// wrap changes, and the writer is single (every reset runs under the flow table's gate).
    /// </summary>
    private int _version;

    private long _activityBucket;

    /// <summary>
    /// Pool-construction shape: the properties are only meaningful after <see cref="Reset"/>,
    /// which every pooled claim performs before the state becomes visible in a flow table.
    /// </summary>
    internal FlowState()
    {
    }

    public FlowKey Key { get; private set; }
    public FlowDecision Decision { get; private set; }
    public long Generation { get; private set; }

    /// <summary>
    /// The activity stamp's bucket, derived to its bucket's start instant. Bucket-quantised: the true
    /// activity instant is inside this bucket, up to one bucket wide.
    /// </summary>
    public DateTimeOffset LastActivityUtc => ActivityBucket.ToUtc(Volatile.Read(ref _activityBucket));

    /// <summary>The raw bucket the sweep compares (diagnostics only).</summary>
    internal long ActivityBucketForDiagnostics => Volatile.Read(ref _activityBucket);

    /// <summary>Stores the bucket of <paramref name="now"/>. One volatile store, no clock read.</summary>
    public void Touch(DateTimeOffset now) => Volatile.Write(ref _activityBucket, ActivityBucket.FromUtc(now));

    /// <summary>Stores an already-published bucket (the warm path's touch).</summary>
    internal void TouchBucket(long bucket) => Volatile.Write(ref _activityBucket, bucket);

    /// <summary>
    /// Captures the state's published triple, or rejects the read when a concurrent
    /// <see cref="Reset"/> overlaps it. The full fences mirror the writer's: <c>Volatile.Read</c>
    /// is acquire-only, so it does not by itself prevent the field loads from floating above it on a
    /// weak memory model (ARM64), which would let a mixed triple validate against the old version.
    /// </summary>
    internal bool TrySnapshot(out FlowStateView view)
    {
        var version = Volatile.Read(ref _version);
        if ((version & 1) != 0)
        {
            view = default;
            return false;
        }

        Interlocked.MemoryBarrier();
        var key = Key;
        var decision = Decision;
        var generation = Generation;
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _version) != version)
        {
            view = default;
            return false;
        }

        view = new FlowStateView(key, decision, generation);
        return true;
    }

    /// <summary>
    /// Re-initializes a pooled instance in place for a new claim. Overwrites every field —
    /// including the activity bucket, which starts a fresh idle window — so a recycled state carries
    /// no trace of its previous flow. The version protocol is what a lock-free reader validates
    /// against: publish odd (rewrite in progress) → full fence → the fields → full fence → publish
    /// even (stable). <c>Volatile.Write</c> is release-only, so without the first fence a
    /// reader could observe the new fields while both of its version reads still see the old even
    /// value — a mixed triple that no key mismatch would catch on a weak memory model.
    /// </summary>
    internal void Reset(FlowKey key, FlowDecision decision, long generation, long activityBucket)
    {
        var version = Volatile.Read(ref _version);
        Volatile.Write(ref _version, version + 1);
        Interlocked.MemoryBarrier();
        Key = key;
        Decision = decision;
        Generation = generation;
        Volatile.Write(ref _activityBucket, activityBucket);
        Interlocked.MemoryBarrier();
        Volatile.Write(ref _version, version + 2);
    }
}
