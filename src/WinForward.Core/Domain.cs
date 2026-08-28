using System.Net;

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

public readonly struct Endpoint : IEquatable<Endpoint>
{
    public Endpoint(AddressFamilyKind addressFamily, IPAddress address, ushort port)
    {
        ArgumentNullException.ThrowIfNull(address);
        var actualFamily = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? AddressFamilyKind.IPv4 : AddressFamilyKind.IPv6;
        if (addressFamily != actualFamily) throw new ArgumentException("Endpoint address family does not match the address.", nameof(addressFamily));
        AddressFamily = addressFamily;
        Address = address;
        Port = port;
    }

    public AddressFamilyKind AddressFamily { get; }
    public IPAddress Address { get; }
    public ushort Port { get; }

    public static Endpoint From(IPAddress address, ushort port)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? AddressFamilyKind.IPv4 : AddressFamilyKind.IPv6, address, port);
    }

    public bool Equals(Endpoint other) => AddressFamily == other.AddressFamily && Port == other.Port && Address.Equals(other.Address);
    public override bool Equals(object? obj) => obj is Endpoint other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(AddressFamily);
        hash.Add(Port);
        hash.Add(Address);
        return hash.ToHashCode();
    }

    public static bool operator ==(Endpoint left, Endpoint right) => left.Equals(right);
    public static bool operator !=(Endpoint left, Endpoint right) => !left.Equals(right);
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

    public FlowKey Reverse() => this with { Local = Remote, Remote = Local };
}

public readonly record struct FlowDecision(FlowAction Action, int? RuleIndex, string? ProxyServerName)
{
    public static FlowDecision Fallback(FlowAction action) => new(action, null, null);
}

public sealed record FlowContext(
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

public sealed class FlowTable
{
    private readonly Dictionary<FlowKey, FlowState> _states = [];
    private readonly Dictionary<TransportTuple, FlowState> _transportIndex = [];
    private readonly Lock _gate = new();
    private readonly int _capacity;
    private long _nextGeneration;

    public FlowTable(int capacity = 65_536)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public bool TryGet(FlowKey key, out FlowState? state)
    {
        lock (_gate)
        {
            return _states.TryGetValue(key, out state) || _transportIndex.TryGetValue(TransportTuple.From(key), out state);
        }
    }

    /// <summary>
    /// Resolves a flow for a packet whose key may differ from the stored key in direction, origin
    /// kind, or origin adapter. A flow is identified by its transport tuple (address family,
    /// protocol, and the local/remote endpoint pair in either orientation); origin kind and origin
    /// adapter are provenance metadata that must not cause a reverse or cross-adapter observation
    /// to be re-evaluated as a new flow. A transport-tuple index resolves either orientation without
    /// scanning the flow table.
    /// </summary>
    public bool TryResolve(FlowKey key, out FlowState? state)
    {
        lock (_gate)
        {
            return TryResolveLocked(key, out state);
        }
    }

    /// <summary>
    /// Atomically resolves an existing flow for <paramref name="key"/> or claims a new flow when no
    /// matching flow exists. The decision factory runs only for a genuinely new flow. When a flow is
    /// already present (including in reverse or cross-adapter orientation) the existing decision is
    /// returned and the factory is not invoked, so policy is evaluated exactly once per logical flow.
    /// </summary>
    public bool TryClaimResolved(FlowKey key, Func<FlowDecision> decide, out FlowState? state)
    {
        lock (_gate)
        {
            if (TryResolveLocked(key, out state)) return state is not null;
            if (_states.Count >= _capacity)
            {
                state = null;
                return false;
            }

            var created = new FlowState(key, decide(), ++_nextGeneration);
            _states.Add(key, created);
            AddToTransportIndex(created);
            state = created;
            return true;
        }
    }

    public FlowState Claim(FlowKey key, Func<FlowDecision> decide)
    {
        if (!TryClaim(key, decide, out var state) || state is null) throw new InvalidOperationException("Flow table capacity has been reached.");
        return state;
    }

    public bool TryClaim(FlowKey key, Func<FlowDecision> decide, out FlowState? state)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(key, out var existing) || _transportIndex.TryGetValue(TransportTuple.From(key), out existing))
            {
                existing.Touch(DateTimeOffset.UtcNow);
                state = existing;
                return true;
            }

            if (_states.Count >= _capacity)
            {
                state = null;
                return false;
            }

            var created = new FlowState(key, decide(), ++_nextGeneration);
            _states.Add(key, created);
            AddToTransportIndex(created);
            state = created;
            return true;
        }
    }

    /// <summary>
    /// Removes flow decisions idle past <paramref name="idleTimeout"/>. An entry whose idle has
    /// elapsed but whose <paramref name="isHeld"/> predicate reports a live holder (e.g. a TCP
    /// redirect session still relaying, or a flow inside its post-teardown grace window) is
    /// skipped without touching <see cref="FlowState.LastActivityUtc"/>, so it expires at its
    /// original idle point once the hold lapses instead of being re-armed. The predicate is only
    /// consulted for idle-elapsed candidates. A null predicate removes every idle entry.
    /// </summary>
    public int RemoveExpired(DateTimeOffset now, TimeSpan idleTimeout, Func<FlowKey, bool>? isHeld = null)
    {
        lock (_gate)
        {
            var expired = _states
                .Where(pair => now - pair.Value.LastActivityUtc >= idleTimeout && (isHeld is null || !isHeld(pair.Key)))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in expired)
            {
                if (_states.Remove(key, out var state)) RemoveFromTransportIndex(state);
            }

            return expired.Length;
        }
    }

    private bool TryResolveLocked(FlowKey key, out FlowState? state)
    {
        if (_states.TryGetValue(key, out state) || _transportIndex.TryGetValue(TransportTuple.From(key), out state))
        {
            state.Touch(DateTimeOffset.UtcNow);
            return true;
        }

        state = null;
        return false;
    }

    private void AddToTransportIndex(FlowState state)
    {
        var tuple = TransportTuple.From(state.Key);
        _transportIndex.Add(tuple, state);
        _transportIndex.TryAdd(tuple.Reverse(), state);
    }

    private void RemoveFromTransportIndex(FlowState state)
    {
        var tuple = TransportTuple.From(state.Key);
        _transportIndex.Remove(tuple);
        _transportIndex.Remove(tuple.Reverse());
    }

    private readonly record struct TransportTuple(
        AddressFamilyKind AddressFamily,
        TransportProtocol Protocol,
        Endpoint Local,
        Endpoint Remote)
    {
        public static TransportTuple From(FlowKey key) => new(key.AddressFamily, key.Protocol, key.Local, key.Remote);
        public TransportTuple Reverse() => this with { Local = Remote, Remote = Local };
    }
}
