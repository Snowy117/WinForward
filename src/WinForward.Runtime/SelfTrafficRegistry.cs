using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WinForward.Core;

namespace WinForward.Runtime;

public sealed class SelfTrafficRegistry : ISelfTrafficGuard
{
    private readonly Dictionary<SelfTrafficKey, long> _entries = [];
    private readonly ConcurrentDictionary<WildcardKey, long> _wildcards = [];
    private readonly Lock _gate = new();
    private long _generation;

    public SelfTrafficToken Register(SelfTrafficKey key)
    {
        lock (_gate)
        {
            var generation = ++_generation;
            _entries[key] = generation;
            if (IsWildcardLocal(key.Local.Address)) _wildcards[WildcardKey.From(key)] = generation;
            return new SelfTrafficToken(this, key, generation);
        }
    }

    public bool IsOwned(FlowContext context)
    {
        var key = SelfTrafficKey.From(context);
        var reverse = key with { Local = key.Remote, Remote = key.Local };
        lock (_gate)
        {
            if (_entries.ContainsKey(key) || _entries.ContainsKey(reverse)) return true;
            return _wildcards.ContainsKey(WildcardKey.From(key)) || _wildcards.ContainsKey(WildcardKey.From(reverse));
        }
    }

    /// <summary>
    /// The wildcard half of <see cref="IsOwned"/> alone: two lock-free probes over the relay-socket
    /// registrations whose local address is any. A relay control socket registers
    /// <c>(protocol, Any:port, remote)</c> before its SYN leaves the host — its socket is bound to a
    /// wildcard local endpoint, so the matcher has to cover whatever source IP the routing stack picks.
    /// Answering this half on the warm entry is what keeps an ephemeral port recycled from a proxied
    /// host flow from resolving that flow's stale state and redirecting WinForward's own control
    /// connection into its own proxy; the exact-tuple half cannot serve that purpose, because it is only
    /// checked before a claim and a claimed state is already the "proven not self" record there.
    /// </summary>
    public bool IsWildcardOwned(FlowContext context)
    {
        var key = SelfTrafficKey.From(context);
        var reverse = key with { Local = key.Remote, Remote = key.Local };
        return _wildcards.ContainsKey(WildcardKey.From(key)) || _wildcards.ContainsKey(WildcardKey.From(reverse));
    }

    private static bool IsWildcardLocal(IPAddressValue address) => address.IsIPv4Any || address.IsIPv6Any;

    private void Remove(SelfTrafficKey key, long generation)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var current) && current == generation)
            {
                _entries.Remove(key);
                if (IsWildcardLocal(key.Local.Address)) _wildcards.TryRemove(WildcardKey.From(key), out _);
            }
        }
    }

    [StructLayout(LayoutKind.Auto)]
    public readonly record struct SelfTrafficKey(TransportProtocol Protocol, Endpoint Local, Endpoint Remote)
    {
        public static SelfTrafficKey From(FlowContext context) => new(context.Key.Protocol, context.Key.Local, context.Key.Remote);
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct WildcardKey(TransportProtocol Protocol, ushort LocalPort, Endpoint Remote)
    {
        public static WildcardKey From(SelfTrafficKey key) => new(key.Protocol, key.Local.Port, key.Remote);
    }

    public sealed class SelfTrafficToken : IDisposable
    {
        private readonly SelfTrafficRegistry _registry;
        private readonly SelfTrafficKey _key;
        private readonly long _generation;
        private int _disposed;

        internal SelfTrafficToken(SelfTrafficRegistry registry, SelfTrafficKey key, long generation)
        {
            _registry = registry;
            _key = key;
            _generation = generation;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _registry.Remove(_key, _generation);
        }
    }
}
