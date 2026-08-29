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
/// (bounded by the adapter count for the driver's lifetime). The map lock guards the lookup
/// itself and is always released before the caller enters the returned gate, so waiting on one
/// adapter's gate never blocks lookups for other adapters.
/// </summary>
internal sealed class NdisAdapterGateMap
{
    private readonly Lock _mapLock = new();
    private readonly Dictionary<nint, NdisNativeCallGate> _gates = [];

    internal NdisNativeCallGate Get(nint adapterHandle)
    {
        lock (_mapLock)
        {
            if (!_gates.TryGetValue(adapterHandle, out var gate))
            {
                gate = new NdisNativeCallGate();
                _gates.Add(adapterHandle, gate);
            }

            return gate;
        }
    }

    internal IReadOnlyDictionary<nint, int> GetMaxConcurrentCalls()
    {
        lock (_mapLock)
        {
            var snapshot = new Dictionary<nint, int>(_gates.Count);
            foreach (var (adapterHandle, gate) in _gates) snapshot.Add(adapterHandle, gate.MaxConcurrentCalls);
            return snapshot;
        }
    }
}
