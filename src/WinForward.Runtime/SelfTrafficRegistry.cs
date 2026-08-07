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
        lock (_gate) return _entries.ContainsKey(key) || _entries.ContainsKey(reverse);
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
