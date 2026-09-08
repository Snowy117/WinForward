using System.Net;
using System.Runtime.InteropServices;

namespace WinForward.Core;

public enum TransportProtocol
{
    Tcp,
    Udp
}

public enum AddressFamilyKind
{
    IPv4,
    IPv6
}

public enum FlowOriginKind
{
    Host,
    Forwarded
}

public enum FlowAction
{
    Proxy,
    Pass,
    Block
}

[StructLayout(LayoutKind.Auto)]
public readonly struct Endpoint : IEquatable<Endpoint>
{
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

public readonly record struct AdapterContext(string? StableId, string? Name, long Generation);

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
        if (local.AddressFamily != remote.AddressFamily) throw new ArgumentException("Flow endpoints must use the same address family.", nameof(remote));
        return new(local.AddressFamily, protocol, local, remote, origin, adapter?.StableId, adapter?.Generation ?? 0);
    }

    /// <summary>Flat mix over the endpoint addresses and ports: one pass, no per-field chaining,
    /// tuned for dictionary keys probed on every packet. Deliberately omits the origin fields that
    /// <see cref="Equals(FlowKey)"/> compares: keys differing only in origin kind or origin adapter
    /// are the same logical flow seen from another orientation and must share a hash bucket — the
    /// same field set TransportTuple hashes for its orientation-agnostic index. An origin-aware
    /// equality over this transport-only hash can collide but never diverge, so the asymmetry is
    /// hash-consistent.</summary>
    public override int GetHashCode() => HashCode.Combine(
        (ulong)Local.Address.Bits,
        (ulong)(Local.Address.Bits >> 64),
        (ulong)Remote.Address.Bits,
        (ulong)(Remote.Address.Bits >> 64),
        Local.Port,
        Remote.Port,
        (byte)AddressFamily,
        (byte)Protocol);

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

public readonly record struct FlowDecision(FlowAction Action, int? RuleIndex, string? ProxyServerName)
{
    public static FlowDecision Fallback(FlowAction action) => new(action, null, null);
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
    public FlowState(FlowKey key, FlowDecision decision, long generation)
    {
        Key = key;
        Decision = decision;
        Generation = generation;
        LastActivityUtc = DateTimeOffset.UtcNow;
    }

    public FlowKey Key { get; }
    public FlowDecision Decision { get; }
    public long Generation { get; }
    public DateTimeOffset LastActivityUtc { get; private set; }

    public void Touch(DateTimeOffset now) => LastActivityUtc = now;
}
