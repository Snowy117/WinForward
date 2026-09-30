namespace WinForward.NdisApi;

/// <summary>
/// The adapter's packet-arrival signal — the <c>SetPacketEvent</c> binding's managed side.
/// <see cref="Wait"/> blocks the caller until the signal is raised or the timeout elapses, and its
/// return value is advisory: the pump re-reads on every wake and on every timeout alike, so a
/// coalesced, spurious or missed signal can only cost one timeout of latency, never a packet.
/// </summary>
public interface INdisPacketArrivalSignal : IDisposable
{
    /// <summary>
    /// Blocks until the signal is raised (true) or <paramref name="timeout"/> elapses (false). A
    /// non-positive timeout returns immediately without blocking.
    /// </summary>
    bool Wait(TimeSpan timeout);
}

/// <summary>
/// An <see cref="INdisPacketArrivalSignal"/> over a caller-owned <see cref="WaitHandle"/> — in
/// production the auto-reset event registered with the driver, which retains a signal raised while
/// no waiter is parked so nothing is lost between loop iterations. Deliberately not
/// Windows-attributed: a bounded wait is exactly what the pump needs on any OS, and keeping it
/// OS-neutral is what puts the production wait entry point inside the allocation gates on this
/// host.
/// </summary>
public sealed class NdisPacketArrivalSignal : INdisPacketArrivalSignal
{
    private readonly Action? _release;
    private int _disposed;

    /// <summary>
    /// Wraps <paramref name="arrival"/> and takes over its lifetime: the signal waits on that handle
    /// and disposes it, running <paramref name="release"/> once beforehand so the driver can never
    /// signal a closed handle.
    /// </summary>
    /// <param name="arrival">The event to wait on. The signal owns its disposal.</param>
    /// <param name="release">
    /// Optional registration release, run once before the handle is disposed — the driver binding's
    /// <c>SetPacketEvent(…, 0)</c> in production.
    /// </param>
    public NdisPacketArrivalSignal(WaitHandle arrival, Action? release = null)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        Handle = arrival;
        _release = release;
    }

    public bool Wait(TimeSpan timeout) => Handle.WaitOne(timeout <= TimeSpan.Zero
        ? 0
        : (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue));

    /// <summary>
    /// The underlying event, for a composite that parks on this signal and another handle at once.
    /// This signal keeps owning the handle: nothing else may dispose it or wait on it afterwards.
    /// </summary>
    public WaitHandle Handle { get; }

    /// <summary>
    /// Runs the release first, then disposes the event, so the driver can never signal a closed
    /// handle. Idempotent; the caller must not dispose while a wait is parked.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _release?.Invoke();
        }
        finally
        {
            Handle.Dispose();
        }
    }
}
