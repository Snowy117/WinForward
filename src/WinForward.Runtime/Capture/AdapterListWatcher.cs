using System.Runtime.Versioning;
using WinForward.NdisApi;

namespace WinForward.Runtime.Capture;

/// <summary>
/// A source of "the NDISRD TCP/IP bound-adapter list was rebuilt" observations (design §3.2 of
/// task 09-07-adapter-list-refresh). <see cref="WaitOne"/> blocks the caller's watcher loop
/// until the list changed (true) or the wait resolved as cancelled (false): the token fired, or
/// the source was disposed while waiting. After a true observation every enumeration handle
/// previously returned by the driver is stale and must be re-enumerated.
/// </summary>
public interface IAdapterListChangeSource : IDisposable
{
    /// <summary>Blocks until the adapter list changed (true) or the wait was cancelled (false).</summary>
    bool WaitOne(CancellationToken cancellationToken);
}

/// <summary>
/// An <see cref="IAdapterListChangeSource"/> backed by the NDISAPI adapter-list-change
/// notification: an unnamed auto-reset Win32 event registered with the driver via
/// <see cref="NdisApiDriver.SetAdapterListChangeEvent(nint)"/>. Auto-reset coalesces signal
/// bursts into one pending observation — the ground truth is the re-enumeration, never the
/// signal count — and a driver signal raised while no waiter is parked stays set, so nothing is
/// lost between watcher-loop iterations. The watcher roots the event for its lifetime so the
/// raw handle handed to the driver stays valid; the registration is released (NULL, official
/// semantics) on <see cref="Dispose"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NdisAdapterListWatcher : IAdapterListChangeSource
{
    private readonly NdisApiDriver? _driver;
    private readonly EventWaitHandle _signalEvent;
    private readonly EventWaitHandle _cancelEvent;
    private int _disposed;

    public NdisAdapterListWatcher(NdisApiDriver driver)
        : this()
    {
        ArgumentNullException.ThrowIfNull(driver);
        _driver = driver;
        // The raw handle stays valid for the whole registration: this watcher roots the
        // EventWaitHandle in a field, and Dispose releases the registration before the event.
#pragma warning disable S3869 // Handing the raw event handle to the driver is the documented ABI here.
        driver.SetAdapterListChangeEvent(_signalEvent.SafeWaitHandle.DangerousGetHandle());
#pragma warning restore S3869
    }

    /// <summary>Driver-free seam over the wait-any/cancel/dispose semantics (unit tests).</summary>
    internal NdisAdapterListWatcher()
    {
        _signalEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        _cancelEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
    }

    /// <summary>
    /// Parks the caller in one blocking <see cref="WaitHandle.WaitAny"/> (never spins) until the
    /// driver signals the adapter event (true) or the wait is cancelled (false) by the token
    /// firing or by <see cref="Dispose"/>. After disposal this returns false immediately.
    /// </summary>
    public bool WaitOne(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        using var registration = cancellationToken.Register(static state => ((EventWaitHandle)state!).Set(), _cancelEvent);
        return WaitForSignal(_signalEvent, _cancelEvent);
    }

    /// <summary>Waits on exactly one of the handles: true resolves the signal, false the cancel.</summary>
    internal static bool WaitForSignal(EventWaitHandle signal, EventWaitHandle cancel) =>
        WaitHandle.WaitAny([signal, cancel]) == 0;

    /// <summary>
    /// Unblocks any parked <see cref="WaitOne"/> with false, releases the driver registration
    /// best-effort (a release failure during shutdown is not actionable — official NULL-release
    /// semantics), and disposes both events. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancelEvent.Set();
        if (_driver is { } driver)
        {
            try
            {
                driver.SetAdapterListChangeEvent(nint.Zero);
            }
            catch (Exception exception)
            {
                // Swallow-by-observation: no logger lives here, and a failed release while the
                // process is shutting down has no remedy.
                GC.KeepAlive(exception);
            }
        }
        _signalEvent.Dispose();
        _cancelEvent.Dispose();
    }
}
