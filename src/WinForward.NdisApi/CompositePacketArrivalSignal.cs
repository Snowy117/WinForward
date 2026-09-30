namespace WinForward.NdisApi;

/// <summary>
/// The pump's arrival wait over two handles: the borrowed driver signal and one pipeline-owned
/// auto-reset event. A decided flow signals the owned event so the pump's next iteration delivers
/// it instead of waiting out the idle bound; the driver handle keeps the packet-arrival wake
/// unchanged, and both share the caller's single timeout so the wait shape stays one bounded park.
/// <para>
/// The wait is latency only, never correctness: the pump re-reads on every wake and every timeout
/// alike, so a coalesced, spurious or missed event costs at most one timeout of delivery latency.
/// </para>
/// </summary>
public sealed class CompositePacketArrivalSignal : INdisPacketArrivalSignal
{
    private readonly NdisPacketArrivalSignal _driver;
    private readonly EventWaitHandle _wake;
    private readonly WaitHandle[] _handles;
    private int _disposed;

    /// <summary>
    /// Composes <paramref name="driver"/> (borrowed: this type disposes it in place of the list
    /// owner, so the driver registration is still released exactly once) with
    /// <paramref name="wake"/> (owned by whoever created it, never by this type).
    /// </summary>
    public CompositePacketArrivalSignal(NdisPacketArrivalSignal driver, EventWaitHandle wake)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(wake);
        _driver = driver;
        _wake = wake;
        _handles = [driver.Handle, wake];
    }

    /// <summary>Raises the pipeline-owned event; a no-op once this composite was disposed.</summary>
    public void Signal()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // The registry disposed the event while a worker was still signalling it; the wait it
            // would have ended is already over.
        }
    }

    public bool Wait(TimeSpan timeout)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        var milliseconds = timeout <= TimeSpan.Zero ? 0 : (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue);
        try
        {
            return WaitHandle.WaitAny(_handles, milliseconds) != WaitHandle.WaitTimeout;
        }
        catch (ObjectDisposedException)
        {
            // Disposal raced the park; the pump's stop flag is what ends the loop, so reporting
            // "not signalled" is the fail-safe answer.
            return false;
        }
    }

    /// <summary>
    /// Releases the driver registration and event; the owned wake event belongs to its creator.
    /// Idempotent, and a caller must not dispose while a wait is parked.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _driver.Dispose();
    }
}
