using System.Diagnostics;

namespace WinForward.E2E.Client;

internal static class Clock
{
    internal static long Now => Stopwatch.GetTimestamp();

    internal static double ToSeconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    internal static long FromSeconds(double seconds) => (long)(seconds * Stopwatch.Frequency);

    internal static long ToNanoseconds(long ticks) => (long)(ticks * (1_000_000_000.0 / Stopwatch.Frequency));

    internal static long ToMicroseconds(long ticks) => (long)(ticks * (1_000_000.0 / Stopwatch.Frequency));
}
