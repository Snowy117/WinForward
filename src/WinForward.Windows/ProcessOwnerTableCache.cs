using WinForward.Core;

namespace WinForward.Windows;

/// <summary>
/// The owner-table snapshot cache: one reusable snapshot slot and one single-flight refresh gate per
/// <see cref="OwnerTableKind"/>, keyed by the instant each caller asked.
/// <para>
/// The coalescing property this exists for: concurrent missers join one in-flight read whose
/// contents are published after the last of them asked, so a burst's own newly-bound sockets are
/// visible to the shared read. A window alone cannot deliver that, because a socket's row appears
/// at bind — microseconds before the packet that triggers the lookup — and can therefore never be
/// in a snapshot taken before the burst.
/// </para>
/// <para>
/// Staleness is bounded per kind, and the bound is the predicate's, not a preference. A TCP row
/// matches the full four-tuple, so a row that survives into a later request describes the same
/// connection; a UDP row matches the local port alone, so a recycled port inside the window would
/// attribute a flow to the previous process (fail-open) and UDP therefore never reuses a snapshot
/// older than the request. UDP still coalesces onto an epoch published at or after its request.
/// </para>
/// <para>
/// Every search runs under the same per-kind gate as the read, because the reader's slot is
/// refilled in place rather than replaced: that is what lets a scan allocate nothing, and it is
/// also why a record is only read while it is the slot's newest one. A fill invalidates the slot as
/// soon as it begins, so a fill that fails after that point leaves the slot unavailable until a
/// later fill completes; a failure before the fill begins (the row-count validation the parser runs
/// first) leaves the slot and its previous contents untouched, exactly as if the read had not
/// happened.
/// </para>
/// </summary>
internal sealed class ProcessOwnerTableCache
{
    /// <summary>The default TCP reuse window: an exact-four-tuple reuse inside it is effectively impossible under TIME_WAIT.</summary>
    public const int DefaultWindowMs = 300;

    private readonly IProcessOwnerTableReader _reader;
    private readonly Func<DateTimeOffset> _clock;
    private readonly OwnerTableSnapshot?[] _snapshots = new OwnerTableSnapshot?[4];
    private readonly Lock[] _gates = [new(), new(), new(), new()];
    private long _readCount;

    public ProcessOwnerTableCache(IProcessOwnerTableReader reader, int windowMs = DefaultWindowMs, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegative(windowMs);
        _reader = reader;
        WindowMs = windowMs;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Owner-table fills this cache published (the coalescing series' source). Only a completed fill
    /// counts: a read that throws before it fills — the row-count validation — leaves the slot and its
    /// previous contents untouched, exactly as if the read had not happened.
    /// </summary>
    public long ScanCount => Interlocked.Read(ref _readCount);

    /// <summary>
    /// Optional per-scan sink for process-wide diagnostics: invoked once per successful scan.
    /// Composition sets it once at startup, before any consumer can look up. Diagnostics only — the
    /// sink must not throw and never influences cache behaviour.
    /// </summary>
    public Action? ReadSink { get; set; }

    /// <summary>The reuse window in milliseconds; <c>0</c> keeps coalescing and drops reuse.</summary>
    private int WindowMs { get; }

    /// <summary>
    /// The owning PID for <paramref name="key"/> when this caller's request instant is the cache's
    /// own clock reading — the shape every product caller uses.
    /// </summary>
    public uint? Lookup(FlowKey key) => Lookup(key, _clock());

    /// <summary>
    /// The owning PID for <paramref name="key"/>, or null when no read this cache is allowed to
    /// reuse holds a unique owner. <paramref name="requestInstant"/> is the moment this caller
    /// asked: a slot filled before it may only answer a positive TCP row, and any miss falls
    /// through to a read, so a flow whose socket bound after the last read still gets a real scan.
    /// </summary>
    public uint? Lookup(FlowKey key, DateTimeOffset requestInstant)
    {
        if (OwnerTable.KindOf(key) is not { } kind) return null;
        var slot = (int)kind;
        lock (_gates[slot])
        {
            var snapshot = _snapshots[slot];
            if (snapshot is not null && snapshot.IsUsable)
            {
                // A fill published at or after the request saw every socket that existed when the
                // caller asked, so it may answer negatively as well.
                if (snapshot.TakenUtc >= requestInstant) return snapshot.Table.Lookup(key);

                // Anything older may only confirm a row: see the type's staleness note.
                if (ReusesSnapshot(kind) && IsFresh(snapshot, _clock()) && snapshot.Table.Lookup(key) is { } reused)
                {
                    return reused;
                }
            }

            var table = _reader.Read(kind);
            if (!table.IsAvailable) return null;
            var published = new OwnerTableSnapshot(table, _clock());
            _snapshots[slot] = published;
            _ = Interlocked.Increment(ref _readCount);
            ReadSink?.Invoke();
            return published.Table.Lookup(key);
        }
    }

    /// <summary>
    /// Only the TCP kinds may answer from a slot filled before the request; see the type's
    /// staleness note. UDP kinds keep the coalescing half and lose the reuse half.
    /// </summary>
    private static bool ReusesSnapshot(OwnerTableKind kind) => kind is OwnerTableKind.Tcp4 or OwnerTableKind.Tcp6;

    private bool IsFresh(OwnerTableSnapshot snapshot, DateTimeOffset now) =>
        now - snapshot.TakenUtc <= TimeSpan.FromMilliseconds(WindowMs);

    /// <summary>
    /// One published read: the table and the instant it was taken. <see cref="IsUsable"/> is false
    /// when the table is unavailable, and an unusable snapshot is never published (a "no table on
    /// this platform" answer must not be cached as though it were a read). The table may be the
    /// reader's reusable slot, so a record is only consulted while it is the slot's newest one.
    /// </summary>
    private sealed class OwnerTableSnapshot(OwnerTable table, DateTimeOffset takenUtc)
    {
        public OwnerTable Table { get; } = table;
        public DateTimeOffset TakenUtc { get; } = takenUtc;
        public bool IsUsable => Table.IsAvailable;
    }
}
