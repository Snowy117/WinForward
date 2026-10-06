using Microsoft.Extensions.Logging;
using WinForward.Runtime.Logging;

namespace WinForward.Runtime;

/// <summary>
/// Rate-limited (5 s) warn gate for transient adapter read retries (R7): per-retry invocation is
/// naturally bounded by the pump's retry budget, and this window keeps a persistently flapping
/// adapter from flooding the console. The window anchors on the last emitted warn — a suppressed
/// retry does not extend it — and the CAS admits exactly one writer when retries from several
/// adapters race. The clock is a <see cref="TimeProvider"/> so the window semantics stay testable
/// without real time.
/// </summary>
#pragma warning disable MA0182 // Consumed by WinForward.Cli through InternalsVisibleTo (Program.cs wires it into the pump's retry logging) and by AdapterTransientRetryLogGateTests; the analyzer only sees usages inside this assembly and cannot see IVT consumers.
internal sealed class AdapterTransientRetryLogGate(ILogger logger, TimeProvider? timeProvider = null)
{
    private static readonly long s_windowTicks = TimeSpan.FromSeconds(5).Ticks;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _lastLogTicks;

    public void Log(string adapterStableId, string adapterFriendlyName, int nativeError, int attempt)
    {
        var now = _time.GetUtcNow().Ticks;
        var last = Interlocked.Read(ref _lastLogTicks);
        if (now - last < s_windowTicks) return;
        if (Interlocked.CompareExchange(ref _lastLogTicks, now, last) != last) return;
        RuntimeLog.AdapterRetry(logger, adapterStableId, adapterFriendlyName, nativeError, attempt);
    }
}
#pragma warning restore MA0182
