using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.Logging;

namespace WinForward.Runtime;

/// <summary>
/// The dispatcher side of the deferred-attribution contract: everything the pipeline needs to
/// evaluate a policy and to hand a decided packet to the executor, without the pipeline reaching
/// into the dispatcher's own state.
/// </summary>
internal interface IFlowAttributionHost
{
    FlowTable Flows { get; }

    ISetupExecutor SetupExecutor { get; }

    IPacketActionExecutor Executor { get; }

    IReadOnlyDictionary<string, ProxyTarget> Targets { get; }

    PolicySnapshot Policy { get; }

    /// <summary>The shared attribution call, so the pipeline and the inline path cannot drift.</summary>
    ValueTask<FlowContext> AttributeAsync(FlowContext context, CancellationToken cancellationToken);

    /// <summary>The dispatcher's capacity-block counter plus its rate-limited warn.</summary>
    void LogCapacityBlock(FlowContext context);

    /// <summary>The dispatcher's debug <c>flow.created</c> line for a flow the pipeline just created.</summary>
    void LogFlowCreated(FlowContext context, long generation, FlowDecision decision);
}

/// <summary>
/// Process attribution off the capture pump: admission retains a packet and returns, a pooled setup
/// worker runs attribution and policy evaluation, and the owning adapter's pump delivers the
/// decision in arrival order and claims the flow last.
/// <para>
/// Three properties are structural rather than defensive. An entry is admitted, delivered and
/// drained only on its own adapter's pump, because the executor's batched lanes are keyed by
/// adapter and are only safe inside that pump's serialized batch loop. A failure arm never claims,
/// so a failed flow cannot acquire a decision. The batch is never detached from its entry: the
/// ring slot is emptied exactly once under the gate and a per-batch finally blocks whatever an
/// executor throw left unexecuted, so no lease can be stranded between the two.
/// </para>
/// </summary>
internal sealed class FlowAttributionPipeline : IAsyncDisposable
{
    private readonly FlowAttributionPendingIndex _index;
    private readonly IFlowAttributionHost _host;
    private readonly QuiescenceScope _scope = new();
    private readonly RuntimeLogThrottle _pendingRejectedWarn = new(TimeSpan.FromSeconds(5));
    private readonly RuntimeLogThrottle _flowFullTrace = new(TimeSpan.FromSeconds(5));
    private readonly List<PendingFlowAttribution> _sweepScratch = [];
    private readonly ILogger _logger;
    private long _admissions;
    private long _attributions;
    private long _blockedPackets;
    private long _refusedPackets;
    private long _retainedPackets;
    private int _disposed;

    public FlowAttributionPipeline(NativeBufferPool pool, IFlowAttributionHost host, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _logger = logger ?? NullLogger.Instance;
        _index = new FlowAttributionPendingIndex(pool, host.Flows);
    }

    /// <summary>The wake registry the decided-entry signal goes to; null when no adapter registered one.</summary>
    public FlowAttributionWakeRegistry? Wake { get; set; }

    public int PendingCount => _index.ActiveCount;

    public int DecidedDepth => _index.DecidedDepth;

    public long Admissions => Interlocked.Read(ref _admissions);

    public long AttributionsOnSetupWorker => Interlocked.Read(ref _attributions);

    public long BlockedPacketCount => Interlocked.Read(ref _blockedPackets);

    public long RefusedPacketCount => Interlocked.Read(ref _refusedPackets);

    /// <summary>Packets retained across every admitted entry (the admission workload, not the entry count).</summary>
    public long RetainedPacketCount => Interlocked.Read(ref _retainedPackets);

    /// <summary>The live failure cooldowns; a shutdown cancellation must never add one.</summary>
    public int CooldownCount => _index.CooldownCount;

    /// <summary>Claims the flow table refused at capacity; each one keeps its entry for a later retry.</summary>
    public long ClaimFailedCount => _index.ClaimFailedCount;

    /// <summary>Second entries whose transport tuple already carried a decision.</summary>
    public long ReAdmissionCount => _index.ReAdmissionCount;

    /// <summary>
    /// Retains <paramref name="packet"/> when its flow needs attribution and the pipeline can carry
    /// it; the caller then completes the lease as deferred (or delivers the returned decided entry
    /// inline) and returns. Runs on the pump thread and never attributes inline.
    /// </summary>
    public AttributionAdmission Admit(in CapturedFlowPacket packet)
    {
        var outcome = _index.Admit(packet, DateTimeOffset.UtcNow, out var createdEntry, out _);
        switch (outcome)
        {
            case AttributionAdmission.Deferred:
                // One admission per pending entry, not per packet: the entries are what a worker
                // attributes and counts.
                if (createdEntry is not null)
                {
                    _ = Interlocked.Increment(ref _admissions);
                    _ = Interlocked.Increment(ref _retainedPackets);
                    Launch(createdEntry);
                }
                else
                {
                    _ = Interlocked.Increment(ref _retainedPackets);
                }

                break;
            case AttributionAdmission.Inline:
                break;
            case AttributionAdmission.BlockedFailed
                or AttributionAdmission.BlockedSealed
                or AttributionAdmission.BlockedPending
                or AttributionAdmission.BlockedFlowFull
                or AttributionAdmission.BlockedCooldown:
                _ = Interlocked.Increment(ref _refusedPackets);
                LogRefusal(outcome, packet.Context);
                break;
        }

        return outcome;
    }

    /// <summary>
    /// Delivers every decided entry of <paramref name="adapterHandle"/>, in decision order. Called
    /// from the pump's per-iteration (and loop-exit) callback so the frames it dispatches leave in
    /// the same iteration's batched flush. Synchronous by construction: the callback it runs inside
    /// is an action, and a pending executor call parks the pump exactly as the inline path does.
    /// </summary>
    public void DeliverDecided(nint adapterHandle)
    {
        if (!_index.HasDecided) return;
        while (_index.TryDequeueDecided(adapterHandle, out var entry, out var batch))
        {
            Deliver(entry!, batch!);
        }
    }

    /// <summary>
    /// Reclaims entries whose verdict never landed inside the retention TTL and returns how many
    /// were reclaimed; the sweeper leg. The scratch list is reused, so a tick that reclaims nothing
    /// allocates nothing — the shape the sweep allocation gate measures.
    /// </summary>
    public int RemoveExpired(DateTimeOffset now)
    {
        _sweepScratch.Clear();
        _index.RemoveExpired(now, _sweepScratch);
        var reclaimed = _sweepScratch.Count;
        long released = 0;
        foreach (var entry in _sweepScratch)
        {
            // A reclaimed entry's retained packets never reach the executor, so they are released
            // fail-closed here and counted with every other fail-closed drop; the ring size is read
            // before the release empties it.
            released += entry.Count;
            FlowAttributionPendingIndex.DisposeReclaimed(entry);
        }

        if (released > 0) _ = Interlocked.Add(ref _blockedPackets, released);
        _sweepScratch.Clear();
        return reclaimed;
    }

    private void Launch(PendingFlowAttribution entry)
    {
        var item = _host.SetupExecutor.RentItem(RunAttributionAsync);
        item._attribution._entry = entry;
        if (_host.SetupExecutor.TryEnqueue(item)) return;
        // A refused enqueue recycles the item without settling anything, so the pipeline settles
        // its own entry: the flow fails closed at the next drain rather than staying pending.
        _ = Interlocked.Increment(ref _refusedPackets);
        RuntimeCounters.Shared.Increment(RuntimeCounters.AttributionSetupRejected);
        _index.MarkFailed(entry, shutdown: false);
        SignalWake(entry);
    }

    private async Task RunAttributionAsync(SetupWorkItem item)
    {
        var entry = item._attribution._entry!;
        if (!_scope.TryEnter(out var lease))
        {
            _index.MarkFailed(entry, shutdown: true);
            SignalWake(entry);
            return;
        }

        try
        {
            _ = Interlocked.Increment(ref _attributions);
            var context = await _host.AttributeAsync(entry.Context, _scope.Token).ConfigureAwait(false);
            _index.MarkDecided(entry, context, Evaluate(context));
        }
        catch (OperationCanceledException) when (_scope.Token.IsCancellationRequested)
        {
            // Shutdown unwinds without a tombstone: no cooldown, no failure counter.
            _index.MarkFailed(entry, shutdown: true);
        }
        catch (Exception)
        {
            // A genuine failure is one flow's: it is counted and fails closed, never the capture run.
            RuntimeCounters.Shared.Increment(RuntimeCounters.AttributionSetupFailed);
            _index.MarkFailed(entry, shutdown: false);
        }
        finally
        {
            lease.Dispose();
            SignalWake(entry);
        }
    }

    private void SignalWake(PendingFlowAttribution entry) => Wake?.Signal(entry.AdapterHandle);

    private FlowDecision Evaluate(FlowContext context) =>
        context.Key.Origin == FlowOriginKind.Forwarded ? _host.Policy.EvaluateForwarded(context) : _host.Policy.EvaluateHost(context);

    private void Deliver(PendingFlowAttribution entry, List<RetainedPacket> batch)
    {
        if (_index.TryTakeFailed(entry, batch, DateTimeOffset.UtcNow, out var failed))
        {
            _ = Interlocked.Add(ref _blockedPackets, failed);
            BlockEach(entry, batch);
            return;
        }

        while (true)
        {
            var taken = _index.TakeAll(entry, batch, out var state);
            if (taken == 0)
            {
                if (state == PendingAttributionState.Decided) break;
                return;
            }

            // A claim the flow table refused at capacity is not a lost flow: the packets executed
            // under this entry's decision keep it, and everything that arrives now blocks
            // fail-closed until a later claim succeeds.
            if (entry.ClaimFailed)
            {
                _ = Interlocked.Add(ref _blockedPackets, taken);
                BlockEach(entry, batch);
                continue;
            }

            try
            {
                ExecuteBatch(entry, batch);
            }
            catch
            {
                // The aborted batch's remainder is already blocked and released; re-queueing the
                // entry lets the next drain retry the claim instead of stranding it until the TTL.
                _index.Requeue(entry);
                throw;
            }
        }

        var claim = _index.Claim(entry, out var generation);
        switch (claim)
        {
            case AttributionClaim.Claimed:
                if (entry.Decision is { } decision) _host.LogFlowCreated(entry.Context, generation, decision);
                break;
            case AttributionClaim.AlreadyAttributed:
                break;
            case AttributionClaim.CapacityBlocked:
                RuntimeCounters.Shared.Increment(RuntimeCounters.FlowCapacityBlock);
                _host.LogCapacityBlock(entry.Context);
                break;
            default:
                throw new InvalidOperationException($"Unhandled attribution claim '{claim}'.");
        }
    }

    /// <summary>
    /// Executes one batch in arrival order. The ring slot was already emptied under the gate, so a
    /// packet executes at most once; a throw blocks and releases whatever the batch had not yet
    /// reached, so nothing detached from the entry is ever stranded.
    /// </summary>
    private void ExecuteBatch(PendingFlowAttribution entry, List<RetainedPacket> batch)
    {
        var started = 0;
        Exception? failure = null;
        try
        {
            while (started < batch.Count)
            {
                var retained = batch[started];
                // Counted as started before the call: a call that threw had already completed the
                // lease and reached the executor, so re-blocking it would report a drop twice.
                started++;
                Execute(entry, retained);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            for (var index = started; index < batch.Count; index++)
            {
                BlockRetained(entry, batch[index]);
                _ = Interlocked.Increment(ref _blockedPackets);
            }

            batch.Clear();
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void Execute(PendingFlowAttribution entry, RetainedPacket retained)
    {
        var decision = entry.Decision ?? throw new InvalidOperationException("A decided entry must carry a decision.");
        var packet = Rebuild(entry, retained);
        try
        {
            switch (decision.Action)
            {
                case FlowAction.Pass:
                    Complete(packet, PacketDisposition.Pass);
                    Run(_host.Executor.PassAsync(packet));
                    break;
                case FlowAction.Proxy when ResolveTarget(decision) is { } target:
                    Complete(packet, PacketDisposition.ProxyConsumed);
                    Run(_host.Executor.ProxyAsync(packet, target, CancellationToken.None));
                    break;
                case FlowAction.Proxy
                    or FlowAction.Block:
                    // Block, and a proxy decision whose target no longer resolves: both fail closed,
                    // exactly as the dispatcher's inline path does.
                    Complete(packet, PacketDisposition.Block);
                    Run(_host.Executor.BlockAsync(packet));
                    break;
            }
        }
        finally
        {
            retained.Frame.Dispose();
        }
    }

    private static void Complete(CapturedFlowPacket packet, PacketDisposition disposition) => packet.Lease.TryComplete(disposition);

    /// <summary>The named target, or null when the decision names none the configuration still holds. One dictionary probe over the resolved-target table the executor's TCP and UDP branches both consume.</summary>
    private ProxyTarget? ResolveTarget(FlowDecision decision) =>
        decision.TargetName is not null && _host.Targets.TryGetValue(decision.TargetName, out var target) ? target : null;

    /// <summary>
    /// Waits for one executor call. The delivery runs inside the pump's synchronous per-iteration
    /// callback — the same place the inline path calls the executor — and every warm executor shape
    /// completes inline, so this is normally a plain result read; a genuinely pending call parks the
    /// dedicated pump thread exactly as the pump's own handler wait does.
    /// </summary>
    private static void Run(ValueTask pending)
    {
        if (pending.IsCompletedSuccessfully)
        {
#pragma warning disable VSTHRD002 // The warm shape: a plain allocation-free result read, mirroring NdisCapturePump.InvokeHandler.
            pending.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            return;
        }

#pragma warning disable VSTHRD002 // The capture pump owns this thread and blocks on its handler by contract; the executor's warm shapes complete inline, so no thread-pool thread is parked here.
        pending.AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    private void BlockEach(PendingFlowAttribution entry, List<RetainedPacket> batch)
    {
        foreach (var retained in batch) BlockRetained(entry, retained);
        batch.Clear();
    }

    private void BlockRetained(PendingFlowAttribution entry, RetainedPacket retained)
    {
        var packet = Rebuild(entry, retained);
        try
        {
            Complete(packet, PacketDisposition.Block);
            Run(_host.Executor.BlockAsync(packet));
        }
        finally
        {
            retained.Frame.Dispose();
        }
    }

    /// <summary>
    /// Rebuilds a dispatchable packet from a retained copy. Deliberately without a
    /// <see cref="NativeFrameHandle"/>: the capture slot was recycled the moment the admission
    /// returned, so the executor must take its rented-copy branch.
    /// </summary>
    private static CapturedFlowPacket Rebuild(PendingFlowAttribution entry, RetainedPacket retained) =>
        new(new PacketLease(retained.Frame.Memory[..retained.Length]), entry.Context, retained.Metadata, retained.PacketSequence, retained.FlowGeneration, Layout: retained.Layout);

    private void LogRefusal(AttributionAdmission outcome, FlowContext context)
    {
        // Two independently throttled arms with their own levels and events.
        // ReSharper disable once ConvertIfStatementToSwitchStatement // A switch would have to enumerate all seven outcomes for two logging arms.
        if (outcome == AttributionAdmission.BlockedPending && _pendingRejectedWarn.ShouldEmit())
        {
            var key = context.Key;
            FlowLog.FlowAttributionPendingRejected(_logger, key.Protocol, key.Origin, key.Local, key.Remote, _index.ActiveCount);
        }

        if (outcome == AttributionAdmission.BlockedFlowFull && _flowFullTrace.ShouldEmit())
        {
            var key = context.Key;
            FlowLog.FlowAttributionFlowFull(_logger, key.Protocol, key.Local, key.Remote);
        }
    }

    /// <summary>
    /// Seals admission, fails every pending entry closed (blocked, never silently dropped), joins
    /// the in-flight worker leases and releases the retained copies.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _scope.DrainAsync().ConfigureAwait(false);
            return;
        }

        var pending = new List<PendingFlowAttribution>();
        _index.Seal(pending);
        var batch = new List<RetainedPacket>();
        foreach (var entry in pending)
        {
            var taken = _index.TakeAll(entry, batch, out _);
            _ = Interlocked.Add(ref _blockedPackets, taken);
            BlockEach(entry, batch);
        }

        await _scope.DrainAsync().ConfigureAwait(false);
    }
}
