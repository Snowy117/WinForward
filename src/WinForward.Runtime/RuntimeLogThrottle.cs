namespace WinForward.Runtime;

/// <summary>
/// Per-event-site wall-clock throttle for rate-limited diagnostic logging: at most one caller per
/// window is allowed to emit, the first occurrence always emits, and suppressed callers simply do
/// nothing (each call site still increments its own <see cref="RuntimeCounters"/> counter, so the
/// aggregate stays observable even while individual lines are suppressed). One shared instance per
/// event site (owned by the long-lived component that emits the event) replaces the historical
/// bare <c>_last...LogTicks</c> + CAS idiom: check-first so a suppressed call never allocates the
/// message or the structured fields, matching the logging contract that rate-limiting must stay
/// out of packet disposition and hot-path allocation budgets.
/// </summary>
public sealed class RuntimeLogThrottle
{
    private readonly long _windowTicks;
    private long _lastEmitTicks;

    /// <param name="window">The minimum wall-clock interval between two emissions; must be positive.</param>
    public RuntimeLogThrottle(TimeSpan window)
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window), window, "The throttle window must be positive.");
        _windowTicks = window.Ticks;
    }

    /// <summary>
    /// True for the single caller allowed to emit in the current window (CAS-guarded, so
    /// concurrent callers elect exactly one winner); false for everyone else. The very first call
    /// after construction always returns true.
    /// </summary>
    public bool ShouldEmit()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastEmitTicks);
        return now - last >= _windowTicks && Interlocked.CompareExchange(ref _lastEmitTicks, now, last) == last;
    }
}
