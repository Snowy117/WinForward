using System.Runtime.InteropServices;

namespace WinForward.E2E.Client.Arms;

[StructLayout(LayoutKind.Auto)]
internal readonly struct PersistentPlan
{
    internal PersistentPlan(int intervalMilliseconds, int idleSeconds, int payloadBytes, uint expectedBytes, long responseTimeoutTicks)
    {
        IntervalMilliseconds = intervalMilliseconds;
        IdleSeconds = idleSeconds;
        PayloadBytes = payloadBytes;
        ExpectedBytes = expectedBytes;
        ResponseTimeoutTicks = responseTimeoutTicks;
    }

    internal int IntervalMilliseconds { get; }

    internal int IdleSeconds { get; }

    internal int PayloadBytes { get; }

    internal uint ExpectedBytes { get; }

    internal long ResponseTimeoutTicks { get; }
}

[StructLayout(LayoutKind.Auto)]
internal readonly struct PersistentSchedule
{
    internal PersistentSchedule(long startTicks, long deadlineTicks, long intervalTicks, int slots, int idleStartIndex, int idleEndIndex)
    {
        StartTicks = startTicks;
        DeadlineTicks = deadlineTicks;
        IntervalTicks = intervalTicks;
        Slots = slots;
        IdleStartIndex = idleStartIndex;
        IdleEndIndex = idleEndIndex;
    }

    internal long StartTicks { get; }

    internal long DeadlineTicks { get; }

    private long IntervalTicks { get; }

    internal int Slots { get; }

    internal int IdleStartIndex { get; }

    internal int IdleEndIndex { get; }

    internal bool HasIdleWindow => IdleStartIndex < Slots;

    internal long ScheduledIdleTicks => HasIdleWindow ? (IdleEndIndex - IdleStartIndex + 1) * IntervalTicks : 0;

    internal long IntendedTicks(int index) => StartTicks + (index * IntervalTicks);
}
