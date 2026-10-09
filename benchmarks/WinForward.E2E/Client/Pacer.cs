using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinForward.E2E.Client;

[StructLayout(LayoutKind.Auto)]
internal readonly struct Pacer
{
    private readonly double _ticksPerRequest;
    private readonly long _startTicks;

    internal Pacer(double ratePerSecond, long startTicks)
    {
        _startTicks = startTicks;
        _ticksPerRequest = ratePerSecond > 0 ? Stopwatch.Frequency / ratePerSecond : 0;
    }

    internal long IntendedTicks(long index) => _startTicks + (long)(index * _ticksPerRequest);

    internal static void WaitUntil(long deadlineTicks, CancellationToken cancellationToken)
    {
        while (true)
        {
            var remainingTicks = deadlineTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingMilliseconds = remainingTicks * 1000.0 / Stopwatch.Frequency;
            switch (remainingMilliseconds)
            {
                case > 8:
                    Thread.Sleep((int)remainingMilliseconds - 6);
                    break;
                case > 0.2:
                    Thread.Sleep(0);
                    break;
                default:
                    Thread.SpinWait(32);
                    break;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    internal static async ValueTask WaitUntilAsync(long deadlineTicks, CancellationToken cancellationToken)
    {
        var remainingTicks = deadlineTicks - Stopwatch.GetTimestamp();
        var remainingMilliseconds = remainingTicks * 1000.0 / Stopwatch.Frequency;
        if (remainingMilliseconds > 20)
        {
            await Task.Delay((int)remainingMilliseconds - 15, cancellationToken).ConfigureAwait(false);
        }

#pragma warning disable S6966, VSTHRD103, MA0042 // Sub-millisecond open-loop pacing; Task.Delay cannot hold these instants on Windows.
        WaitUntil(deadlineTicks, cancellationToken);
#pragma warning restore S6966, VSTHRD103, MA0042
    }
}
