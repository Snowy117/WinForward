using WinForward.Configuration;

namespace WinForward.Runtime;

/// <summary>
/// Rate-limited (5 s) warn gate for transient adapter read retries (R7): per-retry invocation is
/// naturally bounded by the pump's retry budget, and this window keeps a persistently flapping
/// adapter from flooding the console. The window anchors on the last emitted warn — a suppressed
/// retry does not extend it — and the CAS admits exactly one writer when retries from several
/// adapters race. The ticks source is injectable so the window semantics stay testable without
/// real time.
/// </summary>
internal sealed class AdapterTransientRetryLogGate(IRuntimeLogger logger, Func<long>? ticksProvider = null)
{
    private static readonly long s_windowTicks = TimeSpan.FromSeconds(5).Ticks;
    private readonly Func<long> _nowTicks = ticksProvider ?? DefaultTicks;
    private long _lastLogTicks;

    private static long DefaultTicks() => DateTime.UtcNow.Ticks;

    public void Log(string adapterStableId, string adapterFriendlyName, int nativeError, int attempt)
    {
        var now = _nowTicks();
        var last = Interlocked.Read(ref _lastLogTicks);
        if (now - last < s_windowTicks) return;
        if (Interlocked.CompareExchange(ref _lastLogTicks, now, last) != last) return;
        logger.Event(RuntimeLogLevel.Warn, "adapter.retry",
            new RuntimeLogField("adapter", adapterStableId),
            new RuntimeLogField("name", adapterFriendlyName),
            new RuntimeLogField("nativeError", nativeError),
            new RuntimeLogField("attempt", attempt));
    }
}
