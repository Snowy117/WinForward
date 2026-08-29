using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinForward.Windows;

/// <summary>
/// Scoped Windows timer-resolution upgrade for a capture run. The NDIS capture pump polls an
/// empty adapter queue with a 1 ms delay, but <c>Task.Delay</c> rounds that up to the default
/// system timer resolution (~15.6 ms) unless the period is explicitly lowered, which amplifies
/// ndisrd driver-queue overflow under bursts (task 08-28-udp-loss-design-flaws, R6). Creating
/// this scope calls winmm <c>timeBeginPeriod(1)</c> so the 1 ms poll delay is real (~1–2 ms);
/// disposing it calls <c>timeEndPeriod(1)</c> exactly once. A failed begin degrades the run to
/// the default resolution instead of aborting it: <see cref="IsEnabled"/> reports the outcome
/// and the caller decides whether to warn.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class HighResolutionTimerScope : IDisposable
{
    private const uint PeriodMilliseconds = 1;
    private const uint TimerNoError = 0;

    private readonly Func<uint, uint> _endPeriod;
    private int _disposed;

    /// <summary>
    /// True when <c>timeBeginPeriod(1)</c> succeeded and disposal will restore the default
    /// timer resolution; false when the begin call failed or threw (the scope then runs
    /// disabled and never calls <c>timeEndPeriod</c>).
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>Requests 1 ms timer resolution from winmm for the scope's lifetime.</summary>
    public HighResolutionTimerScope()
        : this(Native.TimeBeginPeriod, Native.TimeEndPeriod)
    {
    }

    /// <summary>
    /// Test seam over the winmm begin/end pair: the delegates receive the requested period and
    /// return the native MMRESULT (0 = TIMERR_NOERROR). A throwing begin leaves the scope
    /// disabled, because degraded timer resolution must never abort a capture run.
    /// </summary>
    internal HighResolutionTimerScope(Func<uint, uint> beginPeriod, Func<uint, uint> endPeriod)
    {
        ArgumentNullException.ThrowIfNull(beginPeriod);
        ArgumentNullException.ThrowIfNull(endPeriod);
        _endPeriod = endPeriod;
        try
        {
            IsEnabled = beginPeriod(PeriodMilliseconds) == TimerNoError;
        }
        catch (Exception)
        {
            // winmm is a system library, but a missing export or a failing host must not take
            // the capture loop down; run with the default (~15.6 ms) poll granularity instead.
            IsEnabled = false;
        }
    }

    /// <summary>Restores the default timer resolution; idempotent and a no-op when disabled.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || !IsEnabled) return;
        _endPeriod(PeriodMilliseconds);
    }

    private static partial class Native
    {
        private const string LibraryName = "winmm.dll";

        // MMRESULT is self-describing (TIMERR_NOERROR == 0); these winmm entry points do not
        // set the thread last-error, so SetLastError stays off.
        [LibraryImport(LibraryName, EntryPoint = "timeBeginPeriod")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        internal static partial uint TimeBeginPeriod(uint period);

        [LibraryImport(LibraryName, EntryPoint = "timeEndPeriod")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        internal static partial uint TimeEndPeriod(uint period);
    }
}
