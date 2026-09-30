using WinForward.NdisApi;

namespace WinForward.Runtime;

/// <summary>
/// The pipeline's wake registry: one composite arrival signal per adapter handle, each pairing that
/// adapter's borrowed driver signal with an event this registry owns. A decided entry signals its
/// own adapter's event so that adapter's pump delivers it on the next iteration instead of waiting
/// out the idle bound.
/// <para>
/// The registry owns only the events it created. The composites are handed to the capture loop in
/// place of the driver signals and dispose those (releasing the driver registration exactly once);
/// the registry never touches them, so an adapter whose driver registration was refused simply has
/// no entry and the pipeline's signal becomes a no-op. One event is kept per adapter handle and
/// reused across generations, so an adapter refresh replaces the composite without accumulating
/// handles; <see cref="Dispose"/> releases them after every pump has stopped.
/// </para>
/// </summary>
internal sealed class FlowAttributionWakeRegistry : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<nint, CompositePacketArrivalSignal> _signals = [];
    private readonly Dictionary<nint, EventWaitHandle> _wakes = [];
    private int _disposed;

    /// <summary>
    /// Composes <paramref name="driver"/> into a composite under <paramref name="adapterHandle"/>
    /// and returns it for the capture loop to install and dispose.
    /// </summary>
    public CompositePacketArrivalSignal Register(nint adapterHandle, NdisPacketArrivalSignal driver)
    {
        lock (_gate)
        {
            if (!_wakes.TryGetValue(adapterHandle, out var wake))
            {
                wake = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
                _wakes.Add(adapterHandle, wake);
            }

            var composite = new CompositePacketArrivalSignal(driver, wake);
            _signals[adapterHandle] = composite;
            return composite;
        }
    }

    /// <summary>Wakes the pump that owns <paramref name="adapterHandle"/>, if one registered.</summary>
    public void Signal(nint adapterHandle)
    {
        lock (_gate)
        {
            if (_disposed != 0 || !_signals.TryGetValue(adapterHandle, out var signal)) return;
            signal.Signal();
        }
    }

    /// <summary>The number of registered adapters; for diagnostics and tests.</summary>
    public int Count
    {
        get { lock (_gate) return _signals.Count; }
    }

    /// <summary>The wake events this registry owns; one per adapter handle, reused across generations.</summary>
    public int OwnedEventCount
    {
        get { lock (_gate) return _wakes.Count; }
    }

    public void Dispose()
    {
        EventWaitHandle[] events;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            events = [.. _wakes.Values];
            _wakes.Clear();
            _signals.Clear();
        }

        foreach (var handle in events) handle.Dispose();
    }
}
