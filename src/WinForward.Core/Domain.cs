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

public sealed class FlowState
{
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
    public DateTimeOffset LastActivityUtc { get; private set; }

    public void Touch(DateTimeOffset now) => LastActivityUtc = now;

    /// <summary>
    /// Re-initializes a pooled instance in place for a new claim. Overwrites every field —
    /// including <see cref="LastActivityUtc"/>, which starts a fresh idle window — so a recycled
    /// state carries no trace of its previous flow.
    /// </summary>
    internal void Reset(FlowKey key, FlowDecision decision, long generation)
    {
        Key = key;
        Decision = decision;
        Generation = generation;
        LastActivityUtc = DateTimeOffset.UtcNow;
    }
}
