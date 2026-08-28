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
