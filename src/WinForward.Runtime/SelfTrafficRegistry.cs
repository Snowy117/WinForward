using System.Net;
using WinForward.Core;

namespace WinForward.Runtime;

public sealed class SelfTrafficRegistry : ISelfTrafficGuard
{
    private readonly Dictionary<SelfTrafficKey, long> _entries = [];
    private readonly Lock _gate = new();
    private long _generation;

    public SelfTrafficToken Register(SelfTrafficKey key)
    {
        lock (_gate)
        {
            var generation = ++_generation;
            _entries[key] = generation;
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

            // A socket bound to Any/IPv6Any (e.g. the UDP relay transport binds 0.0.0.0) emits
            // packets whose source IP is chosen by routing, not the bind address. Match such
            // registrations by port + remote regardless of the observed source IP. Entry counts are
            // tiny (a few sockets), so the scan is not a hot-path concern.
            foreach (var entry in _entries.Keys)
            {
                if (entry.Protocol != key.Protocol) continue;
                if (MatchesWildcard(entry, key) || MatchesWildcard(entry, reverse)) return true;
            }
            return false;
        }
    }

    private static bool MatchesWildcard(SelfTrafficKey registered, SelfTrafficKey observed)
    {
        if (registered.Local.Port != observed.Local.Port) return false;
        if (registered.Remote != observed.Remote) return false;
        return registered.Local.Address.Equals(IPAddress.Any) || registered.Local.Address.Equals(IPAddress.IPv6Any);
    }

    private void Remove(SelfTrafficKey key, long generation)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var current) && current == generation) _entries.Remove(key);
        }
    }

    public readonly record struct SelfTrafficKey(TransportProtocol Protocol, Endpoint Local, Endpoint Remote)
    {
        public static SelfTrafficKey From(FlowContext context) => new(context.Key.Protocol, context.Key.Local, context.Key.Remote);
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
