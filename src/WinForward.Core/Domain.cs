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

    public bool MatchesPeerIgnoringScope(Endpoint other) => Port == other.Port && AddressFamily == other.AddressFamily && Address.Bits == other.Address.Bits;

    public override int GetHashCode() => HashCode.Combine(Port, Address);

    public static bool operator ==(Endpoint left, Endpoint right) => left.Equals(right);
    public static bool operator !=(Endpoint left, Endpoint right) => !left.Equals(right);

    public override string ToString() => AddressFamily == AddressFamilyKind.IPv6
        ? $"[{Address}]:{Port}"
        : $"{Address}:{Port}";
}

/// <summary>
/// The logical identity of a packet flow: the transport tuple (family, protocol, both endpoints),
/// the origin kind, and the interned adapter the packet was observed on. The key is packed so it fits
/// one 64-byte cache line and compares with integer operations only — no reference-typed field exists,
/// so no string comparison can appear on any lookup path, and <see cref="Local"/>/<see cref="Remote"/>
/// are materialized only by the cold consumers that need an <see cref="Endpoint"/>.
/// <para>
/// The bit layout is exact: four address halves (32 bytes), the adapter generation (8), both scope
/// ids (8), both ports (4), the adapter slot (2) and the family/protocol/origin bytes (3) = 57 bytes,
/// which 8-byte alignment rounds to 64. Nothing further fits — adding a field fails
/// <c>FlowKeyFitsOneCacheLine</c>.
/// </para>
/// <para>
/// Equality compares the adapter as its interned slot plus the enumeration generation, so a key
/// rebuilt after an adapter refresh is unequal to the stored one exactly as it was when the field
/// was the adapter's stable ID. The hash deliberately does not include either: origin variants of
/// one transport tuple must share a bucket (see <see cref="FlowHash"/>).
/// </para>
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct FlowKey
{
    // The packed values are get-only auto-properties (below), so their backing fields carry the
    // layout the class doc describes; the three discriminator bytes stay plain fields because their
    // accessors narrow them to enums.
    private readonly byte _addressFamily;
    private readonly byte _protocol;
    private readonly byte _origin;

    public static FlowKey Create(Endpoint local, Endpoint remote, TransportProtocol protocol, FlowOriginKind origin) =>
        Create(local, remote, protocol, origin, AdapterSlotTable.NoSlot, 0);

    /// <summary>
    /// Builds a key for an interned capture-scope adapter. The slot and the generation must come
    /// from the same <c>WindowsAdapter</c> instance the packet was classified on, so a key can
    /// never mix one enumeration's slot with another enumeration's generation.
    /// </summary>
    public static FlowKey Create(Endpoint local, Endpoint remote, TransportProtocol protocol, FlowOriginKind origin, ushort adapterSlot, long adapterGeneration)
    {
        // ReSharper disable once ConvertIfStatementToReturnStatement // Guard-clause + throw reads failure-first; the suggested `cond ? throw ... : value` form has no precedent in this repo.
        if (local.AddressFamily != remote.AddressFamily) throw new ArgumentException("Flow endpoints must use the same address family.", nameof(remote));
        return new FlowKey(local.AddressFamily, protocol, local, remote, origin, adapterSlot, adapterGeneration);
    }

    /// <summary>
    /// Cold-edge/test convenience: resolves an already-interned stable ID through
    /// <paramref name="table"/> and builds the key with it. An adapter the table does not know yields
    /// <see cref="AdapterSlotTable.NoSlot"/> — capture-scope adapters are interned at generation build
    /// and an adapter that cannot be interned is refused there, so no captured packet carries
    /// <see cref="AdapterSlotTable.NoSlot"/> for a real adapter.
    /// </summary>
    public static FlowKey Create(Endpoint local, Endpoint remote, TransportProtocol protocol, FlowOriginKind origin, AdapterSlotTable table, string? stableId, long adapterGeneration)
    {
        ArgumentNullException.ThrowIfNull(table);
        var slot = stableId is not null && table.TryGetSlot(stableId, out var resolved) ? resolved : AdapterSlotTable.NoSlot;
        return Create(local, remote, protocol, origin, slot, adapterGeneration);
    }

    private FlowKey(AddressFamilyKind addressFamily, TransportProtocol protocol, Endpoint local, Endpoint remote, FlowOriginKind origin, ushort adapterSlot, long adapterGeneration)
    {
        _addressFamily = (byte)addressFamily;
        _protocol = (byte)protocol;
        _origin = (byte)origin;
        OriginAdapterSlot = adapterSlot;
        OriginAdapterGeneration = adapterGeneration;
        LocalLow = (ulong)local.Address.Bits;
        LocalHigh = (ulong)(local.Address.Bits >> 64);
        LocalScopeId = local.Address.ScopeId;
        LocalPort = local.Port;
        RemoteLow = (ulong)remote.Address.Bits;
        RemoteHigh = (ulong)(remote.Address.Bits >> 64);
        RemoteScopeId = remote.Address.ScopeId;
        RemotePort = remote.Port;
    }

    public AddressFamilyKind AddressFamily => (AddressFamilyKind)_addressFamily;

    public TransportProtocol Protocol => (TransportProtocol)_protocol;

    public FlowOriginKind Origin => (FlowOriginKind)_origin;

    /// <summary>The interned adapter slot; <see cref="AdapterSlotTable.NoSlot"/> for adapter-less keys.</summary>
    public ushort OriginAdapterSlot { get; }

    /// <summary>The generation of the adapter enumeration the slot was interned from.</summary>
    public long OriginAdapterGeneration { get; }

    /// <summary>Materialized endpoint; cold consumers only (policy, self-traffic, redirect setup, logs).</summary>
    public Endpoint Local => Endpoint.From(new IPAddressValue(new UInt128(LocalHigh, LocalLow), AddressFamily, LocalScopeId), LocalPort);

    /// <summary>Materialized endpoint; cold consumers only (policy, self-traffic, redirect setup, logs).</summary>
    public Endpoint Remote => Endpoint.From(new IPAddressValue(new UInt128(RemoteHigh, RemoteLow), AddressFamily, RemoteScopeId), RemotePort);

    internal ulong LocalLow { get; }
    internal ulong LocalHigh { get; }
    internal ulong RemoteLow { get; }
    internal ulong RemoteHigh { get; }
    internal ushort LocalPort { get; }
    internal ushort RemotePort { get; }
    internal uint LocalScopeId { get; }
    internal uint RemoteScopeId { get; }

    /// <summary>Flat mix over the endpoint addresses and ports via <see cref="FlowHash"/>: one pass,
    /// no per-field chaining, tuned for dictionary keys probed on every packet. Deliberately omits
    /// the origin fields that <see cref="Equals(FlowKey)"/> compares: keys differing only in origin
    /// kind or origin adapter are the same logical flow seen from another orientation and must share
    /// a hash bucket — the same field set backs the flow table's orientation-agnostic transport
    /// index, whose key delegates to the same expression. An origin-aware equality over this
    /// transport-only hash can collide but never diverge, so the asymmetry is hash-consistent.</summary>
    public override int GetHashCode() => FlowHash.CombinePacked(_addressFamily, _protocol, LocalLow, LocalHigh, LocalPort, RemoteLow, RemoteHigh, RemotePort);

    /// <summary>Cheapest discriminators first; addresses last because they are the widest fields.
    /// The adapter is compared as its interned slot plus the enumeration generation, so the
    /// adapter-recreation semantics the generation exists for are unchanged.</summary>
    public bool Equals(FlowKey other) =>
        _protocol == other._protocol &&
        _addressFamily == other._addressFamily &&
        _origin == other._origin &&
        OriginAdapterSlot == other.OriginAdapterSlot &&
        OriginAdapterGeneration == other.OriginAdapterGeneration &&
        LocalPort == other.LocalPort &&
        RemotePort == other.RemotePort &&
        LocalLow == other.LocalLow &&
        LocalHigh == other.LocalHigh &&
        RemoteLow == other.RemoteLow &&
        RemoteHigh == other.RemoteHigh &&
        LocalScopeId == other.LocalScopeId &&
        RemoteScopeId == other.RemoteScopeId;

    public FlowKey Reverse() => new(AddressFamily, Protocol, Remote, Local, Origin, OriginAdapterSlot, OriginAdapterGeneration);

    /// <summary>
    /// The packed form of the reverse-observation test: this key's endpoints are <paramref name="other"/>'s
    /// swapped, in the same family and protocol. Served without materializing an
    /// <see cref="Endpoint"/>, which is what keeps the warm UDP response path free of four 48-byte
    /// copies per packet.
    /// </summary>
    public bool IsReverseOf(FlowKey other) =>
        _addressFamily == other._addressFamily &&
        _protocol == other._protocol &&
        LocalLow == other.RemoteLow &&
        LocalHigh == other.RemoteHigh &&
        LocalScopeId == other.RemoteScopeId &&
        LocalPort == other.RemotePort &&
        RemoteLow == other.LocalLow &&
        RemoteHigh == other.LocalHigh &&
        RemoteScopeId == other.LocalScopeId &&
        RemotePort == other.LocalPort;

    public override string ToString() =>
        $"FlowKey {{ AddressFamily = {AddressFamily}, Protocol = {Protocol}, Local = {Local}, Remote = {Remote}, Origin = {Origin}, OriginAdapterSlot = {OriginAdapterSlot}, OriginAdapterGeneration = {OriginAdapterGeneration} }}";
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
    internal static int Combine(AddressFamilyKind addressFamily, TransportProtocol protocol, Endpoint local, Endpoint remote) => CombinePacked(
        (byte)addressFamily,
        (byte)protocol,
        (ulong)local.Address.Bits,
        (ulong)(local.Address.Bits >> 64),
        local.Port,
        (ulong)remote.Address.Bits,
        (ulong)(remote.Address.Bits >> 64),
        remote.Port);

    /// <summary>
    /// The one hash expression behind both entry points: the four 64-bit address halves, both ports,
    /// the family and the protocol, in that order. <see cref="Combine"/> is this function over a
    /// materialized endpoint pair, so the packed and materialized forms are the same eight values by
    /// construction and cannot drift.
    /// </summary>
    internal static int CombinePacked(byte family, byte protocol, ulong localLow, ulong localHigh, ushort localPort, ulong remoteLow, ulong remoteHigh, ushort remotePort) => HashCode.Combine(
        localLow,
        localHigh,
        remoteLow,
        remoteHigh,
        localPort,
        remotePort,
        family,
        protocol);

    /// <summary>
    /// The order-independent form of <see cref="Combine"/>: the two endpoints are ordered by
    /// <c>(address bits, port)</c> first, so a packet and its reverse select the same value. The flow
    /// table's warm cache keys its slots on this, which is why it can serve exactly what the two
    /// orientation-aware dictionary probes serve (the table holds at most one state per transport
    /// tuple, pinned by <c>FlowTableTransportTupleIsUniqueAcrossOrigins</c>). It delegates to
    /// <see cref="CombinePacked"/> so the cache's slot function stays the same transport-only mix the
    /// dictionaries bucket by — collisions are fine, divergence is not.
    /// </summary>
    internal static int CombineCanonical(AddressFamilyKind addressFamily, TransportProtocol protocol, Endpoint first, Endpoint second) => CombineCanonicalPacked(
        (byte)addressFamily,
        (byte)protocol,
        (ulong)first.Address.Bits,
        (ulong)(first.Address.Bits >> 64),
        first.Port,
        (ulong)second.Address.Bits,
        (ulong)(second.Address.Bits >> 64),
        second.Port);

    /// <summary>
    /// The packed form of <see cref="CombineCanonical"/>: the total order is the high address half,
    /// then the low half, then the port (the order <c>UInt128.CompareTo</c> defines), and equal
    /// endpoints order either way, so a key and its reverse always select the same slot.
    /// </summary>
    internal static int CombineCanonicalPacked(byte family, byte protocol, ulong firstLow, ulong firstHigh, ushort firstPort, ulong secondLow, ulong secondHigh, ushort secondPort)
    {
        bool firstBefore;
        if (firstHigh != secondHigh) firstBefore = firstHigh < secondHigh;
        else if (firstLow != secondLow) firstBefore = firstLow < secondLow;
        else firstBefore = firstPort <= secondPort;
        return firstBefore
            ? CombinePacked(family, protocol, firstLow, firstHigh, firstPort, secondLow, secondHigh, secondPort)
            : CombinePacked(family, protocol, secondLow, secondHigh, secondPort, firstLow, firstHigh, firstPort);
    }
}

public readonly record struct FlowDecision(FlowAction Action, int? RuleIndex, string? TargetName)
{
    public static FlowDecision Fallback(FlowAction action) => new(action, RuleIndex: null, TargetName: null);
}

/// <summary>
/// What a classified packet's consumers need to know about the flow it belongs to: the packed
/// <see cref="FlowKey"/>, the interned adapter identity the packet was observed on, and — once a
/// claim attributed it — the interned process identity. The two metadata references replace four
/// per-packet strings (and the remote port, which is read straight off the key), so the struct a
/// dispatch copies 2-3 times per packet stays small and copies no string. Policy, logging and the
/// executors read the derived properties below by name.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct FlowContext(
    FlowKey Key,
    AdapterMetadata? Adapter,
    ProcessMetadata? Process)
{
    /// <summary>The interned adapter's GUID-primary stable ID; null for an adapter-less key.</summary>
    public string? AdapterId => Adapter?.StableId;

    /// <summary>The interned adapter's friendly name; null for an adapter-less key.</summary>
    public string? AdapterName => Adapter?.FriendlyName;

    /// <summary>The attributed process name; null on warm packets and unresolved attributions.</summary>
    public string? ProcessName => Process?.ProcessName;

    /// <summary>The attributed process path; logged only when the configuration includes it.</summary>
    public string? ProcessPath => Process?.ProcessPath;

    /// <summary>The remote port, read off the packed key without materializing an endpoint.</summary>
    public ushort RemotePort => Key.RemotePort;
}

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
