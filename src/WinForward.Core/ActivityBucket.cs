namespace WinForward.Core;

/// <summary>
/// The activity-time quantum shared by every warm-path activity stamp (flow states, TCP redirect
/// associations, UDP sessions). A stamp is the bucket its instant falls in — quantised <em>down</em> —
/// and a sweep compares buckets, so a warm hit writes one integer and never reads a clock.
/// <para>
/// Width: 500 ms. The binding half of the retention contract is "at least eight buckets per retention
/// window"; the shortest accepted idle timeout is <c>MinimumUdpSessionIdleSeconds = 5</c>
/// (<c>ConfigurationLimits.cs</c>), whose eighth is 0.625 s, so the historical "at least one second"
/// lower bound cannot hold at the configuration floor and 500 ms is the finer of the two constraints
/// that still keeps ten buckets in a five-second window.</para>
/// <para>
/// Retention is never early: a state stamped at <c>t0</c> retires at age
/// <c>idleTimeout + w − (t0 mod w)</c>, i.e. in <c>(idleTimeout, idleTimeout + w]</c>, because the
/// sweep's comparison is a strict <c>&lt;</c> on buckets while a stamp is quantised down. A
/// non-positive idle timeout keeps its "retire everything on this call" meaning: the cutoff is
/// <see cref="long.MaxValue"/> instead of a derived bucket.
/// </para>
/// </summary>
public static class ActivityBucket
{
    /// <summary>Ticks per bucket: 500 ms of <see cref="TimeSpan"/> ticks.</summary>
    private const long TicksPerBucket = TimeSpan.TicksPerMillisecond * 500;

    /// <summary>The bucket a UTC instant in ticks falls in, flooring (not truncating) before the epoch.</summary>
    private static long FromUtcTicks(long utcTicks)
    {
        var bucket = utcTicks / TicksPerBucket;
        // Integer division rounds toward zero; an instant before the epoch must floor instead, or two
        // instants a bucket apart would share one bucket and a backwards clock step could retire early.
        if (utcTicks < 0 && bucket * TicksPerBucket != utcTicks) bucket--;
        return bucket;
    }

    /// <summary>The bucket a UTC instant falls in.</summary>
    public static long FromUtc(DateTimeOffset utc) => FromUtcTicks(utc.UtcTicks);

    /// <summary>The start instant of a bucket. Derived timestamps therefore land on a bucket boundary.</summary>
    public static DateTimeOffset ToUtc(long bucket) => new(bucket * TicksPerBucket, TimeSpan.Zero);

    /// <summary>
    /// The one sweep cutoff rule, in bucket space and computed from the caller's instant: an entry is
    /// expired when its stamp bucket is strictly below this. A non-positive idle timeout keeps its
    /// "retire everything on this call" meaning — the derived form would keep an entry stamped in the
    /// current bucket alive to the next edge, and the drain call sites depend on retiring every entry.
    /// </summary>
    public static long Cutoff(DateTimeOffset now, TimeSpan idleTimeout) =>
        idleTimeout <= TimeSpan.Zero ? long.MaxValue : FromUtcTicks(now.UtcTicks - idleTimeout.Ticks);
}

/// <summary>
/// The one activity clock per runtime composition: <see cref="Current"/> is a plain volatile read that
/// every warm path consumes, and the clock is advanced by the pump's per-iteration callback, by each
/// claim and by each sweep, so the per-packet path never calls <see cref="TimeProvider.GetUtcNow"/>.
/// </summary>
public sealed class ActivityBucketClock(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private long _bucket = ActivityBucket.FromUtc((timeProvider ?? TimeProvider.System).GetUtcNow());

    /// <summary>The last published bucket. No clock call, no lock, no allocation.</summary>
    public long Current => Volatile.Read(ref _bucket);

    /// <summary>
    /// Diagnostics-only tick sink, null in production (the <c>FlowTable.HoldProbe</c> shape): the tick
    /// site reads it once into a local behind a plain null check, so a Release run carries no cost.
    /// </summary>
    internal Action? TickProbe { get; set; }

    /// <summary>Reads the clock exactly once and publishes the bucket that instant falls in.</summary>
    public long Tick()
    {
        var bucket = ActivityBucket.FromUtc(_timeProvider.GetUtcNow());
        Volatile.Write(ref _bucket, bucket);
        TickProbe?.Invoke();
        return bucket;
    }

    /// <summary>
    /// Publishes the bucket of an instant the caller already holds, so a sweep compares against the
    /// clock it was handed instead of reading the clock a second time.
    /// </summary>
    public void Publish(DateTimeOffset utc) => Volatile.Write(ref _bucket, ActivityBucket.FromUtc(utc));
}
