using WinForward.NdisApi;
using WinForward.Runtime.Logging;

namespace WinForward.Runtime.Capture;

/// <summary>
/// The worker half of the kernel pass cache: one tick every <see cref="s_tickInterval"/> advances the
/// lease, drains the observation ring, applies at most one table change, and emits the periodic
/// summary. Every driver call in the whole cache happens here or in a lifecycle transition — never on
/// a pump thread.
/// </summary>
internal sealed partial class NdisKernelPassCache
{
    /// <summary>Worker cadence: four lease/apply passes per second.</summary>
    private static readonly TimeSpan s_tickInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long an observed flow must stay quiet before its packet is one-shot rather than worth a rule.</summary>
    private static readonly TimeSpan s_installDelay = TimeSpan.FromSeconds(1);

    /// <summary>How long a rule survives without a counter advance; also the recycled-tuple window.</summary>
    private static readonly TimeSpan s_idleLease = TimeSpan.FromSeconds(2);

    /// <summary>How often the aggregate <c>kernel.passCache</c> line is emitted.</summary>
    internal static readonly TimeSpan s_summaryInterval = TimeSpan.FromSeconds(10);

    /// <summary>Consecutive unreadable-table ticks before the cache disables itself (10 s).</summary>
    internal const int ReadFailureLimit = 40;

    /// <summary>Consecutive refused-apply ticks before the cache disables itself (2 s).</summary>
    internal const int ApplyFailureLimit = 8;

    /// <summary>
    /// One worker tick, in the design's order: activity from the driver's per-rule counters, idle
    /// expiry (skipped when the table is unreadable — keeping rules on a table we cannot read is the
    /// safe choice, because the table itself still bypasses only pass flows), draining ready
    /// observations, at most one table application, then the summary.
    /// </summary>
    internal void Tick()
    {
        lock (_workerGate)
        {
            if (Volatile.Read(ref _state) != StateRunning) return;
            TickCore(_timeProvider.GetUtcNow().UtcTicks);
        }
    }

    private void TickCore(long now)
    {
        var readFailed = false;
        if (_tableMatchesDesired && RuleCount > 0)
        {
            readFailed = !ReadActivity(now, out var readStatus, out var readError);
            if (readFailed)
            {
                _readFailureStreak++;
                if (_readFailureStreak >= ReadFailureLimit)
                {
                    Disable("read", readStatus, readError);
                    return;
                }
            }
            else
            {
                _readFailureStreak = 0;
            }
        }

        if (!readFailed) ExpireIdleRules(now);
        DrainCandidates(now);
        ApplyIfDirty();
        if (Volatile.Read(ref _state) != StateRunning) return;
        EmitSummaryIfDue(now);
    }

    private bool ReadActivity(long nowTicks, out NdisKernelFilterStatus status, out int nativeError)
    {
        status = _table.ReadRulePacketCounts(_applyRules.AsSpan(0, RuleCount), _applyCounts.AsSpan(0, RuleCount), out nativeError);
        if (status != NdisKernelFilterStatus.Success)
        {
            _ = Interlocked.Increment(ref _readFailures);
            LogFault("read", status, nativeError);
            return false;
        }

        for (var index = 0; index < RuleCount; index++)
        {
            ref var rule = ref _rules[index];
            var delta = _applyCounts[index] - rule.LastCounter;
            rule.LastCounter = _applyCounts[index];
            if (delta <= 0) continue;

            rule.LastActiveTicks = nowTicks;
            _bypassedTotal += delta;
            if (rule.CountedActive) continue;
            rule.CountedActive = true;
            _intervalActive++;
        }

        return true;
    }

    /// <summary>
    /// Retires every rule whose counters have not advanced within the idle lease, clearing its marker
    /// so the flow's next pass packet re-arms the cache. Retirement is what bounds both the rule set
    /// and the window in which a recycled 5-tuple could inherit a rule.
    /// </summary>
    private void ExpireIdleRules(long nowTicks)
    {
        var leaseTicks = s_idleLease.Ticks;
        for (var index = 0; index < RuleCount;)
        {
            if (nowTicks - _rules[index].LastActiveTicks < leaseTicks)
            {
                index++;
                continue;
            }

            RetireRuleAt(index);
        }
    }

    /// <summary>
    /// Moves the observations whose install delay has elapsed out of the ring (oldest first, under the
    /// ring lock, without touching the driver) and adds them to the desired set, evicting the
    /// least-recently-active rule when the flow cap is reached.
    /// </summary>
    private void DrainCandidates(long nowTicks)
    {
        var delayTicks = s_installDelay.Ticks;
        _drainedCount = 0;
        lock (_ringGate)
        {
            while (_ringCount > 0)
            {
                if (nowTicks - _ringSeenTicks[_ringTail] < delayTicks) break;
                _drainedRules[_drainedCount] = _ringRules[_ringTail];
                _drainedSeenTicks[_drainedCount] = _ringSeenTicks[_ringTail];
                _drainedCount++;
                _ringTail = Next(_ringTail);
                _ringCount--;
                if (_drainedCount == _drainedRules.Length) break;
            }
        }

        for (var index = 0; index < _drainedCount; index++)
        {
            var rule = _drainedRules[index];
            if (IsInstalled(rule)) continue;
            if (RuleCount == MaxFlows) EvictLeastRecentlyActive();
            _rules[RuleCount] = new CachedRule(rule, _drainedSeenTicks[index]);
            RuleCount++;
            _desiredDirty = true;
        }

        _drainedCount = 0;
    }

    /// <summary>
    /// Applies the desired set at most once per tick: an empty set clears the table, a non-empty one
    /// replaces it whole. This is the coalescing contract — a burst of observations costs one IOCTL,
    /// not one per flow.
    /// </summary>
    private void ApplyIfDirty()
    {
        if (!_desiredDirty) return;

        NdisKernelFilterStatus status;
        int nativeError;
        if (RuleCount == 0)
        {
            status = _table.Reset(out nativeError);
        }
        else
        {
            for (var index = 0; index < RuleCount; index++) _applyRules[index] = _rules[index].Rule;
            status = _table.Replace(_applyRules.AsSpan(0, RuleCount), out nativeError);
        }

        if (status == NdisKernelFilterStatus.Success)
        {
            // A replace resets the driver's per-rule counters, so every baseline restarts at zero.
            for (var index = 0; index < RuleCount; index++) _rules[index].LastCounter = 0;
            _tableMatchesDesired = true;
            _desiredDirty = false;
            _applyFailureStreak = 0;
            _ = Interlocked.Increment(ref _applies);
            return;
        }

        _ = Interlocked.Increment(ref _applyFailures);
        _applyFailureStreak++;
        _tableMatchesDesired = false;
        LogFault("apply", status, nativeError);
        if (_applyFailureStreak >= ApplyFailureLimit) Disable("apply", status, nativeError);
    }

    private void EvictLeastRecentlyActive()
    {
        var victim = 0;
        for (var index = 1; index < RuleCount; index++)
        {
            if (_rules[index].LastActiveTicks < _rules[victim].LastActiveTicks) victim = index;
        }

        RetireRuleAt(victim);
    }

    private void RetireRuleAt(int index)
    {
        ClearMarker(_rules[index].Rule);
        _ = Interlocked.Increment(ref _evictions);
        for (var move = index; move + 1 < RuleCount; move++) _rules[move] = _rules[move + 1];
        RuleCount--;
        _desiredDirty = true;
    }

    private bool IsInstalled(in NdisTcpFlowRule rule)
    {
        for (var index = 0; index < RuleCount; index++)
        {
            if (_rules[index].Rule == rule) return true;
        }

        return false;
    }

    private void EmitSummaryIfDue(long nowTicks)
    {
        if (nowTicks - _lastSummaryTicks < s_summaryInterval.Ticks) return;
        EmitSummary(nowTicks);
    }

    /// <summary>
    /// The periodic aggregate line and the counter mirror. Every figure is a delta since the previous
    /// line; the two ratios are diagnostics of the driver path's share of the work (the denominator is
    /// the packets it accounted for), and are omitted when nothing moved.
    /// </summary>
    private void EmitSummary(long nowTicks)
    {
        var traffic = _table.GetTrafficCounters();
        var captured = traffic.PacketsRead - _lastTraffic.PacketsRead;
        var readCalls = traffic.ReadCalls - _lastTraffic.ReadCalls;
        var sendCalls = traffic.SendCalls - _lastTraffic.SendCalls;
        var reinjected = traffic.PacketsReinjected - _lastTraffic.PacketsReinjected;
        var bypassed = _bypassedTotal - _lastSummarizedBypassed;
        var accounted = captured + bypassed;
        long? crossings = accounted > 0 ? 1000 * (captured + reinjected) / accounted : null;
        long? calls = accounted > 0 ? 1000 * (readCalls + sendCalls) / accounted : null;

        KernelPassCacheLog.PassCacheSummary(_logger, RuleCount, _intervalActive, bypassed, captured, reinjected, crossings, calls);
        _lastTraffic = traffic;
        _lastSummarizedBypassed = _bypassedTotal;
        _intervalActive = 0;
        _lastSummaryTicks = nowTicks;
        for (var index = 0; index < RuleCount; index++) _rules[index].CountedActive = false;
        MirrorCounters();
    }

    private void MirrorCounters()
    {
        Mirror(0, RuntimeCounters.KernelPassCacheBypassed, Interlocked.Read(ref _bypassedTotal));
        Mirror(1, RuntimeCounters.KernelPassCacheApplies, Interlocked.Read(ref _applies));
        Mirror(2, RuntimeCounters.KernelPassCacheEvictions, Interlocked.Read(ref _evictions));
        Mirror(3, RuntimeCounters.KernelPassCacheRejected, Interlocked.Read(ref _rejected));
        Mirror(4, RuntimeCounters.KernelPassCacheMarkerOverflow, Interlocked.Read(ref _markerOverflow));
        Mirror(5, RuntimeCounters.KernelPassCacheReadFailures, Interlocked.Read(ref _readFailures));
        Mirror(6, RuntimeCounters.KernelPassCacheApplyFailures, Interlocked.Read(ref _applyFailures));
        Mirror(7, RuntimeCounters.KernelPassCacheDisabled, Interlocked.Read(ref _disabled));
    }

    private void Mirror(int slot, string key, long value)
    {
        var delta = value - _mirroredAtSummary[slot];
        if (delta == 0) return;
        _mirroredAtSummary[slot] = value;
        _counters.Add(key, delta);
    }

    /// <summary>A single fault line, throttled to one per window; the aggregate counters always move.</summary>
    private void LogFault(string operation, NdisKernelFilterStatus status, int nativeError)
    {
        if (!_faultWarn.ShouldEmit()) return;
        KernelPassCacheLog.PassCacheFault(_logger, operation, status, nativeError == 0 ? null : nativeError);
    }
}
