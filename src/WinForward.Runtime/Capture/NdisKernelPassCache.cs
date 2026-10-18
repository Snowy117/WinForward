using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime.Logging;

namespace WinForward.Runtime.Capture;

/// <summary>
/// A bounded, leased mirror of already-decided policy passes in the driver's static filter table: a
/// TCP flow the dispatcher decided <c>Pass</c> becomes two oriented <c>FILTER_PACKET_PASS</c> rules, so
/// its later packets take the driver's normal network path instead of crossing into user mode and
/// back. The cache never decides anything — it mirrors a decision — and it is PASS-only by
/// construction, so a crash leaves at worst a benign pass-through rule that the next start clears.
/// </summary>
/// <remarks>
/// <para>
/// The cache is an owner of one worker loop and of the driver's table while enabled. Observation runs
/// on the pump thread and costs a marker test plus a bounded ring append — no driver call, no flow
/// lookup, no allocation. Every driver operation, and every mutation of the rule set, runs on the
/// worker; the ring lock is only ever held for an array copy and never across a driver call.
/// </para>
/// <para>
/// A rule outlives its last matched packet by at most the idle lease, which bounds the window in which
/// a recycled TCP 5-tuple could inherit a rule; that residual (and the driver table being global) is
/// why the feature is opt-in.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class NdisKernelPassCache : IKernelPassFlowObserver, IAsyncDisposable
{
    /// <summary>How many flows the mirror holds; two table records per flow.</summary>
    internal const int MaxFlows = 128;

    /// <summary>Observation marker slots; a power of two so the probe chain masks instead of dividing.</summary>
    private const int MarkerSlots = 4_096;

    /// <summary>How many pass observations may wait for their install delay.</summary>
    internal const int CandidateCapacity = 1_024;

    /// <summary>Linear probe bound for one marker; a full chain drops the observation.</summary>
    private const int MarkerProbeLength = 8;

    private const int StateCreated = 0;
    private const int StateRunning = 1;
    private const int StatePaused = 2;
    private const int StateDisabled = 3;

    private const string StartupOperation = "startup";

    private readonly INdisKernelFilterTable _table;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly RuntimeCounters _counters;
    private readonly QuiescenceScope _scope = new();
    private readonly RuntimeLogThrottle _faultWarn = new(TimeSpan.FromSeconds(5));

    // Observation state, shared with the worker.
    private readonly long[] _markers = new long[MarkerSlots];
    private readonly Lock _ringGate = new();
    private readonly NdisTcpFlowRule[] _ringRules = new NdisTcpFlowRule[CandidateCapacity];
    private readonly long[] _ringSeenTicks = new long[CandidateCapacity];

    // Worker-private state: only the worker tick (and the lifecycle transitions, which the runner
    // issues between generations) touches the rule set, the desired/installed flag, and the apply
    // buffers. The worker gate is what makes that true: the tick holds it, and Start/Pause/Resume/
    // Dispose take it before touching the same state. The observation path never does, so a pump can
    // never block on a tick.
    private readonly Lock _workerGate = new();

    private readonly CachedRule[] _rules = new CachedRule[MaxFlows];
    // The last set handed to Replace, in install order. ReadActivity may only run while
    // _tableMatchesDesired is true, which a successful apply sets after refilling this array from
    // _rules; a failed apply clears the flag, so RuleCount and this array agree whenever counters are
    // read.
    private readonly NdisTcpFlowRule[] _applyRules = new NdisTcpFlowRule[MaxFlows];
    private readonly long[] _applyCounts = new long[MaxFlows];
    private readonly NdisTcpFlowRule[] _drainedRules = new NdisTcpFlowRule[CandidateCapacity];
    private readonly long[] _drainedSeenTicks = new long[CandidateCapacity];

    private int _state;
    private int _ringHead;
    private int _ringTail;
    private int _ringCount;
    private int _drainedCount;
    private bool _tableMatchesDesired;
    // Set once the startup reset succeeded: only then does this cache own the table and may clear it
    // again. A failed startup reset disables the cache without touching a table it could not claim.
    private bool _claimedTable;
    private bool _desiredDirty;
    private int _readFailureStreak;
    private int _applyFailureStreak;
    private long _lastSummaryTicks;
    private NdisTrafficCounters _lastTraffic;
    private long _lastSummarizedBypassed;
    private int _intervalActive;
    private long _bypassedTotal;
    private long _applies;
    private long _evictions;
    private long _rejected;
    private long _markerOverflow;
    private long _readFailures;
    private long _applyFailures;
    private long _disabled;
    private readonly long[] _mirroredAtSummary = new long[8];

    internal NdisKernelPassCache(INdisKernelFilterTable table, ILogger? logger = null, TimeProvider? timeProvider = null, RuntimeCounters? counters = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
        _logger = logger ?? NullLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _counters = counters ?? RuntimeCounters.Shared;
        _lastSummaryTicks = _timeProvider.GetUtcNow().UtcTicks;
    }

    /// <summary>True while the cache observes and installs rules.</summary>
    internal bool IsRunning => Volatile.Read(ref _state) == StateRunning;

    /// <summary>True once the cache stopped for good; a disabled cache never retries.</summary>
    internal bool IsDisabled => Volatile.Read(ref _state) == StateDisabled;

    internal int RuleCount { get; private set; }

    internal long BypassedPackets => Interlocked.Read(ref _bypassedTotal);

    internal long Applies => Interlocked.Read(ref _applies);

    internal long Evictions => Interlocked.Read(ref _evictions);

    internal long RejectedObservations => Interlocked.Read(ref _rejected);

    internal long ReadFailures => Interlocked.Read(ref _readFailures);

    internal long ApplyFailures => Interlocked.Read(ref _applyFailures);

    internal int PendingCandidates
    {
        get { lock (_ringGate) return _ringCount; }
    }

    /// <summary>
    /// Starts the cache: the initial <see cref="INdisKernelFilterTable.Reset"/> both clears a previous
    /// run's table and proves the API exists. A missing export or a refused reset leaves the cache
    /// disabled with one warning and never touches capture.
    /// </summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _state, StateRunning, StateCreated) != StateCreated)
        {
            throw new InvalidOperationException("The kernel pass cache is already started.");
        }
        lock (_workerGate)
        {
            var status = _table.Reset(out var nativeError);
            if (status != NdisKernelFilterStatus.Success)
            {
                Disable(StartupOperation, status, nativeError);
                return;
            }

            _tableMatchesDesired = true;
            _claimedTable = true;
            // Open the summary window at start so the first line reports this cache's deltas rather than
            // everything the driver counted since it opened.
            _lastTraffic = _table.GetTrafficCounters();
            _lastSummaryTicks = _timeProvider.GetUtcNow().UtcTicks;
            KernelPassCacheLog.PassCacheReset(_logger, StartupOperation, status, nativeError: null);
            ObjectDisposedException.ThrowIf(!_scope.Run(RunAsync, "kernel.passCache.loop"), this);
        }
    }

    /// <summary>
    /// Clears the table and the cache state because interception is paused (an empty capture scope).
    /// A failed clear keeps the cache paused with the desired set marked unapplied, so the first tick
    /// after <see cref="Resume"/> retries it before any rule is trusted again.
    /// </summary>
    public void Pause()
    {
        lock (_workerGate)
        {
            if (Interlocked.CompareExchange(ref _state, StatePaused, StateRunning) != StateRunning) return;
            ClearCacheState();
            var status = _table.Reset(out var nativeError);
            _tableMatchesDesired = status == NdisKernelFilterStatus.Success;
            _desiredDirty = !_tableMatchesDesired;
            KernelPassCacheLog.PassCacheReset(_logger, "pause", status, nativeError: status == NdisKernelFilterStatus.Success ? null : nativeError);
        }
    }

    /// <summary>Resumes observation into an empty table; the next tick re-applies the (empty) set.</summary>
    public void Resume()
    {
        lock (_workerGate)
        {
            if (Interlocked.CompareExchange(ref _state, StateRunning, StatePaused) != StatePaused) return;
            _desiredDirty = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var previous = Interlocked.Exchange(ref _state, StateDisabled);
        await _scope.DrainAsync().ConfigureAwait(false);
        if (previous == StateCreated || !_claimedTable) return;

        lock (_workerGate)
        {
            EmitSummary(_timeProvider.GetUtcNow().UtcTicks);
            // The driver is still open here: the composition disposes the cache before its other owners,
            // so this is the last chance to clear the table this cache claimed.
            var status = _table.Reset(out var nativeError);
            KernelPassCacheLog.PassCacheReset(_logger, "shutdown", status, nativeError: status == NdisKernelFilterStatus.Success ? null : nativeError);
        }
    }
    /// <summary>
    /// The pump-thread observation hook. A running cache accepts a TCP tuple with two non-zero ports
    /// and an expressible address pair (same family, no scope id, not IPv6 link-local); the first
    /// observation of a tuple claims its marker and queues a candidate. Everything else returns after
    /// a state read.
    /// </summary>
    public void OnPassPacket(in FlowKey key)
    {
        if (Volatile.Read(ref _state) != StateRunning) return;
        if (key.Protocol != TransportProtocol.Tcp) return;
        if (!TryCreateRule(key, out var rule)) return;
        if (!TryMarkRule(rule)) return;
        if (TryAppendObservation(rule, _timeProvider.GetUtcNow().UtcTicks)) return;

        // The ring is full: the flow keeps today's path, and clearing the marker lets a later packet
        // retry once the worker has drained the backlog.
        ClearMarker(rule);
        _ = Interlocked.Increment(ref _rejected);
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(s_tickInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) Tick();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
    }

    /// <summary>
    /// Orders the two endpoints of one flow into the canonical rule order (family bits, then port, as
    /// unsigned values) and refuses a tuple the native record cannot express.
    /// </summary>
    private static bool TryCreateRule(in FlowKey key, out NdisTcpFlowRule rule)
    {
        rule = default;
        var local = key.Local;
        var remote = key.Remote;
        if (local.Port == 0 || remote.Port == 0) return false;

        var localFirst = local.Address.Bits != remote.Address.Bits
            ? local.Address.Bits < remote.Address.Bits
            : local.Port <= remote.Port;
        rule = localFirst
            ? new NdisTcpFlowRule(local.Address, local.Port, remote.Address, remote.Port)
            : new NdisTcpFlowRule(remote.Address, remote.Port, local.Address, local.Port);
        return rule.IsEncodable;
    }

    private bool TryAppendObservation(in NdisTcpFlowRule rule, long seenTicks)
    {
        lock (_ringGate)
        {
            if (_ringCount == CandidateCapacity) return false;
            _ringRules[_ringHead] = rule;
            _ringSeenTicks[_ringHead] = seenTicks;
            _ringHead = Next(_ringHead);
            _ringCount++;
            return true;
        }
    }

    /// <summary>
    /// Claims the tuple's marker slot, or reports that the tuple is already pending or installed (or
    /// that the probe chain is full, which is counted as an overflow). The marker set is the only
    /// deduplication: it is what keeps a flow's pass packets from re-queuing a candidate per packet.
    /// </summary>
    private bool TryMarkRule(in NdisTcpFlowRule rule)
    {
        var marker = MarkerOf(rule);
        var start = (int)((ulong)marker & (MarkerSlots - 1));
        for (var probe = 0; probe < MarkerProbeLength; probe++)
        {
            var index = (start + probe) & (MarkerSlots - 1);
            var current = Volatile.Read(ref _markers[index]);
            if (current == marker) return false;
            if (current != 0) continue;
            if (Interlocked.CompareExchange(ref _markers[index], marker, 0) == 0) return true;
            if (Volatile.Read(ref _markers[index]) == marker) return false;
        }

        _ = Interlocked.Increment(ref _markerOverflow);
        return false;
    }

    private void ClearMarker(in NdisTcpFlowRule rule)
    {
        var marker = MarkerOf(rule);
        var start = (int)((ulong)marker & (MarkerSlots - 1));
        for (var probe = 0; probe < MarkerProbeLength; probe++)
        {
            var index = (start + probe) & (MarkerSlots - 1);
            if (Volatile.Read(ref _markers[index]) != marker) continue;
            _ = Interlocked.CompareExchange(ref _markers[index], 0, marker);
            return;
        }
    }

    /// <summary>FNV-1a over both endpoints' family, address bits and ports; a zero hash maps to 1 (0 means empty).</summary>
    private static long MarkerOf(in NdisTcpFlowRule rule)
    {
        var hash = 14_695_981_039_346_656_037UL;
        hash = Mix(hash, (ulong)rule.AddressA.Family);
        hash = Mix(hash, (ulong)rule.AddressA.Bits);
        hash = Mix(hash, (ulong)(rule.AddressA.Bits >> 64));
        hash = Mix(hash, rule.PortA);
        hash = Mix(hash, (ulong)rule.AddressB.Bits);
        hash = Mix(hash, (ulong)(rule.AddressB.Bits >> 64));
        hash = Mix(hash, rule.PortB);
        return hash == 0 ? 1 : (long)hash;
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        for (var index = 0; index < 8; index++)
        {
            hash ^= (byte)value;
            hash *= 1_099_511_628_211UL;
            value >>= 8;
        }

        return hash;
    }

    private static int Next(int index) => index + 1 == CandidateCapacity ? 0 : index + 1;

    /// <summary>
    /// The fail-safe exit: one error record (or the single startup warning), a final best-effort clear
    /// so no PASS rule is left behind, and an emptied cache state. Terminal for the run — a persistent
    /// native fault must not retry forever.
    /// </summary>
    private void Disable(string operation, NdisKernelFilterStatus status, int nativeError)
    {
        if (Interlocked.Exchange(ref _state, StateDisabled) == StateDisabled) return;
        _ = Interlocked.Increment(ref _disabled);
        var error = nativeError == 0 ? (int?)null : nativeError;
        if (string.Equals(operation, StartupOperation, StringComparison.Ordinal))
        {
            KernelPassCacheLog.PassCacheUnavailable(_logger, error);
        }
        else
        {
            KernelPassCacheLog.PassCacheDisabled(_logger, operation, status, error);
            _ = _table.Reset(out _);
        }

        ClearCacheState();
    }

    private void ClearCacheState()
    {
        lock (_ringGate)
        {
            _ringHead = 0;
            _ringTail = 0;
            _ringCount = 0;
        }

        RuleCount = 0;
        _intervalActive = 0;
        _drainedCount = 0;
        _desiredDirty = false;
        _tableMatchesDesired = false;
        _readFailureStreak = 0;
        _applyFailureStreak = 0;
        Array.Clear(_markers);
    }

    /// <summary>One installed flow: its canonical rule, the lease clock, and the last counter baseline.</summary>
    private struct CachedRule(NdisTcpFlowRule rule, long lastActiveTicks)
    {
        public readonly NdisTcpFlowRule Rule = rule;
        public long LastActiveTicks = lastActiveTicks;
        public long LastCounter;
        public bool CountedActive;
    }
}
