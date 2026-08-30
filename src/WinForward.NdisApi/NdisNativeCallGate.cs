using System.Collections.Concurrent;

namespace WinForward.NdisApi;

internal sealed class NdisNativeCallGate
{
    private readonly object _syncRoot = new();
    private readonly Action? _onContention;
    private int _activeCalls;
    private int _maxConcurrentCalls;

    internal NdisNativeCallGate(Action? onContention = null) => _onContention = onContention;

    internal int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

    internal GateLease Enter()
    {
        if (!Monitor.TryEnter(_syncRoot))
        {
            _onContention?.Invoke();
            Monitor.Enter(_syncRoot);
        }
        var activeCalls = Interlocked.Increment(ref _activeCalls);
        UpdateMaximum(activeCalls);
        return new GateLease(_syncRoot, this);
    }

    internal readonly struct GateLease : IDisposable
    {
        private readonly object _syncRoot;
        private readonly NdisNativeCallGate _owner;

        internal GateLease(object syncRoot, NdisNativeCallGate owner)
        {
            _syncRoot = syncRoot;
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Decrement(ref _owner._activeCalls);
            Monitor.Exit(_syncRoot);
        }
    }

    private void UpdateMaximum(int activeCalls)
    {
        var observed = Volatile.Read(ref _maxConcurrentCalls);
        while (observed < activeCalls)
        {
            var previous = Interlocked.CompareExchange(ref _maxConcurrentCalls, activeCalls, observed);
            if (previous == observed) return;
            observed = previous;
        }
    }
}

/// <summary>
/// Maps NDISAPI adapter enumeration handles to per-adapter native call gates (design D3 of task
/// 08-28-udp-loss-design-flaws): native calls on one adapter stay serialized, while calls on
/// distinct adapters proceed in parallel, so a slow IOCTL on one adapter cannot stall every
/// pump. Gates are keyed by the enumeration handle carried by requests, and the map only grows
/// (bounded by the adapter count for the driver's lifetime). Lookup is lock-free on the fast
/// path (a <see cref="ConcurrentDictionary{TKey,TValue}"/> read); first sight of a handle
/// creates the gate under the dictionary's bucket, and a racing creator may construct a gate
/// that is discarded — harmless, because a gate is a lazily-registered passive object and every
/// caller still resolves to the single stored instance.
/// </summary>
internal sealed class NdisAdapterGateMap
{
    private readonly ConcurrentDictionary<nint, NdisNativeCallGate> _gates = new();

    internal NdisNativeCallGate Get(nint adapterHandle) =>
        _gates.GetOrAdd(adapterHandle, static _ => new NdisNativeCallGate());

    internal IReadOnlyDictionary<nint, int> GetMaxConcurrentCalls()
    {
        var snapshot = new Dictionary<nint, int>(_gates.Count);
        foreach (var (adapterHandle, gate) in _gates) snapshot.Add(adapterHandle, gate.MaxConcurrentCalls);
        return snapshot;
    }
}
