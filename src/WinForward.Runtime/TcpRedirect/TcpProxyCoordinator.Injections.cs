using System.ComponentModel;
using System.Diagnostics;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime.Logging;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// The deferred-injection half of <see cref="TcpProxyCoordinator"/>: the append predicate both data
/// legs consult, the per-(adapter handle, target direction) lane flush the pump's batch-completed
/// callback drives, and the failure tails a degraded batch runs. One coordinator type split across
/// two files purely for the repository's effective-line budget (directory-structure.md, "File Length
/// Ceiling"); the members keep the ownership the design assigns them — the coordinator owns the lanes because
/// it owns the association and the per-flow failure attribution of the frames they hold.
/// </summary>
public sealed partial class TcpProxyCoordinator
{
    private static readonly TimeSpan s_rateLimitedWarnInterval = TimeSpan.FromSeconds(5);

    private readonly RuntimeLogThrottle _redirectOverflowWarn = new(s_rateLimitedWarnInterval);
    private readonly RuntimeLogThrottle _redirectBatchFailedWarn = new(s_rateLimitedWarnInterval);
    private readonly RuntimeLogThrottle _redirectDeferredFailedWarn = new(s_rateLimitedWarnInterval);
    private long _redirectDegradedFlushCount;
    private nint[] _activeRedirectTargets = [];

    /// <summary>
    /// True when the packet was dispatched by a capture pump, which is the only shape allowed to
    /// touch a lane: appends are serialized by the adapter pump's strictly-ordered await chain, not
    /// by a lock. The one data-leg caller off that chain — the setup worker's concurrent-loser
    /// reinjection in <see cref="RunSetupPipelineAsync"/>, reconstructed over the retained SYN copy —
    /// carries a default <c>NativeFrameHandle</c> and must take the immediate send instead.
    /// </summary>
    private static bool IsPumpOwned(in CapturedFlowPacket packet) => packet.NativeFrame.Buffer is not null;

    /// <summary>
    /// Records the adapters of the installed capture scope, the only handles a pump can drain a
    /// redirect lane for. Cold path (scope install); an empty scope means interception is paused, so
    /// nothing may defer to a cross-adapter target.
    /// </summary>
    internal void UpdateRedirectTargets(ReadOnlySpan<nint> activeAdapterHandles) =>
        Volatile.Write(ref _activeRedirectTargets, activeAdapterHandles.ToArray());

    private bool IsActiveRedirectTarget(nint adapterHandle)
    {
        var targets = Volatile.Read(ref _activeRedirectTargets);
        for (var index = 0; index < targets.Length; index++)
        {
            if (targets[index] == adapterHandle) return true;
        }
        return false;
    }

    /// <summary>
    /// True when the frame can be rewritten where it already lies: the pump's capture slot holds the
    /// bytes together with the driver-stamped length, so it serves as both the rewrite target and the
    /// send buffer. A materialized lease means a managed copy already exists and the slot is no longer
    /// the frame's source; a reconstructed packet has no slot at all.
    /// </summary>
    private static bool CanRewriteInPlace(in CapturedFlowPacket packet) =>
        IsPumpOwned(packet) && !packet.Lease.IsMaterialized;

    /// <summary>
    /// Points the data leg at the frame bytes: the capture slot already holds its frame and the
    /// driver-stamped length, while a pooled stage must be filled and length-stamped first because
    /// <c>GetFrame</c> is bounded by that length.
    /// </summary>
    private static Span<byte> StageFrame(in CapturedFlowPacket packet, NdisPacketBuffer buffer, bool inPlace, uint directionFlags, nint adapterHandle)
    {
        if (!inPlace)
        {
            packet.InspectionSpan.CopyTo(buffer.GetFrameStorage());
            buffer.CompleteFrame(packet.InspectionSpan.Length, directionFlags, adapterHandle);
        }
        return buffer.GetFrame();
    }

    /// <summary>
    /// Gives up the frame's staging buffer on every path the lane did not take it over: a rented
    /// pooled copy returns to the pool exactly once, while a capture slot belongs to the pump's batch.
    /// </summary>
    private static void ReleaseStage(NdisPacketBuffer buffer, bool inPlace, bool deferred)
    {
        if (!inPlace && !deferred) buffer.Dispose();
    }

    private bool TryDeferRedirectFrame(CapturedFlowPacket packet, NdisPacketBuffer buffer, bool rented, TcpRedirectAssociation association, bool towardMstcp, nint adapterHandle)
    {
        if (!IsPumpOwned(packet)) return false;
        // A lane is only ever drained by the pump it is keyed on, so a cross-adapter target (a
        // forwarded flow's reverse leg toward its origin adapter) may only defer while that adapter
        // is in the installed capture scope. Once it leaves, the frame keeps the immediate send,
        // which fails on the stale handle and runs the per-flow failure tail — a lane would instead
        // hold the frame, its rental, and its client-visible reset forever.
        if (adapterHandle != packet.Metadata.AdapterHandle && !IsActiveRedirectTarget(adapterHandle)) return false;
        if (!_redirectLanes.TryAppend(adapterHandle, towardMstcp, buffer, rented, association))
        {
            LogRedirectOverflow(adapterHandle, towardMstcp);
            return false;
        }
        return true;
    }

    private void LogRedirectFrameInjected(CapturedFlowPacket packet, TcpRedirectAssociation association, string? reason) =>
        TcpRedirectLog.TcpRedirectInjected(
            _logger, packet.PacketSequence == 0 ? null : packet.PacketSequence, packet.FlowGeneration == 0 ? null : packet.FlowGeneration,
            association.Generation, packet.Context.Key.Local, packet.Context.Key.Remote, reason);

    private void LogReverseFrameInjected(CapturedFlowPacket packet, TcpRedirectAssociation association, string? reason) =>
        TcpRedirectLog.TcpReverseInjected(
            _logger, packet.PacketSequence == 0 ? null : packet.PacketSequence, packet.FlowGeneration == 0 ? null : packet.FlowGeneration,
            association.Generation, packet.Context.Key.Local, packet.Context.Key.Remote, reason);

    /// <summary>
    /// The lane-overflow warn: a frame fell back to the immediate single send because the lane
    /// table or its lane hit a cap (the container counted it). Rate-limited, and reported with the
    /// direction and both bounds — a per-lane frame-cap breach means the iteration-end flush was
    /// missed, a table-cap breach means more concurrent (adapter, direction) keys than the cap
    /// admits. The counter aggregates both causes; the event itself does not say which cap fired.
    /// </summary>
    private void LogRedirectOverflow(nint adapterHandle, bool towardMstcp)
    {
        if (!_redirectOverflowWarn.ShouldEmit()) return;
        var direction = towardMstcp ? "mstcp" : "adapter";
        var laneCapacity = RedirectInjectionLanes.LaneTableCapacity;
        var framesPerLane = RedirectInjectionLanes.LaneFrameCapacity;
        TcpRedirectLog.TcpRedirectDeferredOverflow(_logger, "laneCap", adapterHandle, direction, laneCapacity, framesPerLane);
    }

    /// <summary>
    /// Sends every frame one adapter accumulated, one batched injection per target direction
    /// (toward-MSTCP first, then toward-adapter). The pump's batch-completed callback invokes this
    /// once per iteration and once on loop exit, after the pass flush, so a deferred frame leaves
    /// before the next read and control frames issued during dispatch are never overtaken. A
    /// failed batch is all-or-nothing (the ABI reports no per-packet success count), so every
    /// frame degrades to its own single send, whose failure runs the deferred failure tail.
    /// </summary>
    public void FlushPendingRedirectInjections(nint adapterHandle)
    {
        FlushRedirectLane(adapterHandle, towardMstcp: true);
        FlushRedirectLane(adapterHandle, towardMstcp: false);
    }

    /// <summary>
    /// Debug-only guard for the redirect drain contract: it asserts that no deferred-injection lane
    /// survived its pump iteration, so a missed flush — frames that would never be sent and rented
    /// buffers that would never return — is caught in debug builds and tests. A generation's pumps
    /// are live before the scope-installed callback runs, so this is asserted from tests after an
    /// iteration flush, never from that callback (mirroring <c>DebugAssertNoPendingPasses</c>).
    /// </summary>
    [Conditional("DEBUG")]
    internal void DebugAssertNoPendingRedirectInjections() => _redirectLanes.DebugAssertNoPending();

    private void FlushRedirectLane(nint adapterHandle, bool towardMstcp)
    {
        if (!_redirectLanes.TryTake(adapterHandle, towardMstcp, out var view)) return;
        try
        {
            _injector.InjectBatch(view.Buffers, view.Count, towardMstcp, adapterHandle);
        }
        catch (Exception batchException)
        {
            // Deliberate divergence from the pass flush, which rethrows: a redirect injection
            // failure already ends in a client RST plus a fail-closed association while the
            // pump keeps running, so rethrowing would newly degrade the whole pump for one
            // flow's adapter fault. Every frame is retried individually instead; a lane no
            // wider than the driver's per-request packet budget was delivered nothing by the
            // failed call, so the retry is duplicate-free (InjectBatch documents the caveat for
            // a wider lane).
            Interlocked.Increment(ref _redirectDegradedFlushCount);
            LogRedirectBatchFailed(adapterHandle, towardMstcp, view.Count, batchException);
            for (var index = 0; index < view.Count; index++)
            {
                try
                {
                    _injector.Inject(view.Buffers[index], towardMstcp, adapterHandle, _store.ShutdownToken);
                }
                catch (Exception frameException)
                {
                    // Contained per frame: one association's teardown fault must not strand the
                    // lane's remaining frames, whose rentals the finally still returns.
                    HandleDeferredInjectionFailure(view.Associations[index], adapterHandle, towardMstcp, frameException);
                }
            }
        }
        finally
        {
            // Exactly-once release on success and failure: the rented pooled copies return, the
            // per-frame references clear, and the lane slot frees, so a repeated flush of this key
            // finds no lane instead of re-sending or double-returning.
            _redirectLanes.Release(view);
        }
    }

    /// <summary>
    /// The deferred failure tail: the flush-site counterpart of
    /// <see cref="HandleInjectionFailureAndBlockAsync"/> for a frame whose single send failed after
    /// a degraded batch. It warns rate-limited — the per-frame attribution the batched path moves
    /// from the executor's <c>proxy-blocked</c> line to the flush site — then applies the
    /// established client-visible posture through <see cref="ClientResetInjector"/>: best-effort
    /// reset, then the fail-closed association write. The cold async tails are awaited
    /// synchronously on the pump thread, which already blocks on a pending handler the same way
    /// (<c>NdisCapturePump.InvokeHandler</c>). Never rethrows: a fault of the tail itself is
    /// warned and swallowed so the lane keeps draining.
    /// </summary>
    private void HandleDeferredInjectionFailure(TcpRedirectAssociation association, nint adapterHandle, bool towardMstcp, Exception exception)
    {
        try
        {
            if (_redirectDeferredFailedWarn.ShouldEmit())
            {
                var nativeError = (exception as Win32Exception)?.NativeErrorCode;
                var error = exception.GetType().Name;
                TcpRedirectLog.TcpRedirectDeferredFailed(_logger, "injectionFailure", nativeError, error, adapterHandle, towardMstcp, association.OriginalKey.Local, association.OriginalKey.Remote);
            }
#pragma warning disable VSTHRD002, CA2012 // Deliberate: this flush runs on the pump thread, which already blocks on a pending handler the same way (NdisCapturePump.InvokeHandler); the ValueTask is produced by an async method (task-backed) and is consumed exactly once here, so blocking on it is the documented safe shape and the lane must not release its rentals before the cold teardown tail completes.
            _clientReset.HandleInjectionFailureAsync(association, adapterHandle, towardMstcp, exception).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002, CA2012
        }
        catch (Exception tailException)
        {
            var error = tailException.GetType().Name;
            var detail = tailException.Message;
            TcpRedirectLog.TcpRedirectDeferredFailureTailFaulted(_logger, error, detail);
        }
    }

    /// <summary>
    /// The degraded-batch warn (<c>tcp.redirect.batch-failed</c>), mirroring the pass path's
    /// <c>reinject.pass-failed</c>: one lane's batched send failed and its frames fall back to
    /// single sends. The adapter handle and direction replace the flow key, which one lane spanning
    /// many flows cannot supply.
    /// </summary>
    private void LogRedirectBatchFailed(nint adapterHandle, bool towardMstcp, int frames, Exception exception)
    {
        if (!_redirectBatchFailedWarn.ShouldEmit()) return;
        var direction = towardMstcp ? "mstcp" : "adapter";
        var nativeError = (exception as Win32Exception)?.NativeErrorCode;
        var error = exception.GetType().Name;
        TcpRedirectLog.TcpRedirectBatchFailed(_logger, "batchSend", nativeError, error, adapterHandle, direction, frames);
    }

#pragma warning disable RCS1229 // Deliberate non-async warm entry (hot-path.md → warm-path-dispatch.md, "No async state machines on the steady-state path"): the per-packet path must not pay an async state machine; synchronous failures before the returned ValueTask are part of the warm contract (cold tails live in async helpers).
    private ValueTask<TcpRedirectOutcome> ReinjectExistingFlowDataAsync(CapturedFlowPacket packet, TcpRedirectAssociation association, CancellationToken cancellationToken)
#pragma warning restore RCS1229
    {
        var adapterHandle = packet.Metadata.AdapterHandle;
        var inPlace = CanRewriteInPlace(packet);
        var buffer = inPlace ? packet.NativeFrame.Buffer! : _framePool.Rent();
        var deferred = false;
        try
        {
            var frame = StageFrame(packet, buffer, inPlace, NdisApiAbi.PacketFlagOnReceive, adapterHandle);
            // Read-then-write: advance the client sequence tracker on the pre-rewrite bytes,
            // keeping the reset builder's ack in the client's window.
            TcpSequenceObservation.TrackClientSequence(frame, packet.Layout, association);
            var originalClient = packet.Context.Key.Local;
            var originalServer = association.OriginalKey.Remote;
            if (!TcpFrameRewriter.TryRewriteForwardLeg(frame, packet.Layout, originalClient, originalServer, association, association.TranslatedListenerTuple.Port))
            {
                return FailAssociationAndBlockAsync(association);
            }
            buffer.CompleteFrame(frame.Length, NdisApiAbi.PacketFlagOnReceive, adapterHandle);
            deferred = TryDeferRedirectFrame(packet, buffer, rented: !inPlace, association, towardMstcp: true, adapterHandle);
            if (deferred)
            {
                LogRedirectFrameInjected(packet, association, "batched");
                return ValueTask.FromResult(TcpRedirectOutcome.Injected);
            }
            try
            {
                _injector.Inject(buffer, towardMstcp: true, adapterHandle, cancellationToken);
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromException<TcpRedirectOutcome>(exception);
            }
            catch (OperationCanceledException exception)
            {
                return FailAssociationAndRethrowAsync(association, exception);
            }
            catch (Exception exception)
            {
                return HandleInjectionFailureAndBlockAsync(association, adapterHandle, towardMstcp: true, exception);
            }
            LogRedirectFrameInjected(packet, association, reason: null);
            return ValueTask.FromResult(TcpRedirectOutcome.Injected);
        }
        finally
        {
            ReleaseStage(buffer, inPlace, deferred);
        }
    }

#pragma warning disable RCS1229 // Deliberate non-async warm entry (hot-path.md → warm-path-dispatch.md, "No async state machines on the steady-state path"): the per-packet reverse path must not pay an async state machine; synchronous failures before the returned ValueTask are part of the warm contract.
    public ValueTask<TcpRedirectOutcome> HandleReverseAsync(CapturedFlowPacket packet, TcpRedirectAssociation association, CancellationToken cancellationToken)
#pragma warning restore RCS1229
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract // Deliberate fail-closed capture-boundary guard: Lease is declared non-nullable, but a default CapturedFlowPacket reaches runtime entries with a null lease; CapturedFlowPacketGuards.ThrowLeaseRequired reports the null member (quality-guidelines.md).
        if (packet.Lease is null) CapturedFlowPacketGuards.ThrowLeaseRequired();
        ObjectDisposedException.ThrowIf(_store.IsDisposed, this);

        var key = packet.Context.Key;
        // M5 belt-and-suspenders: this coordinator owns TCP redirect table entries only. The
        // dispatcher already gates the reverse handler to TCP (H1), but a non-TCP packet must never
        // be routed into reverse handling regardless of call context.
        if (key.Protocol != TransportProtocol.Tcp) return ValueTask.FromResult(TcpRedirectOutcome.NotRelevant);

        var original = association.OriginalKey;
        // Host-originated flows terminate on this host (reverse to MSTCP); forwarded flows (client
        // on a VM/remote side) must be sent back to the origin adapter instead.
        var towardMstcp = original.Origin == FlowOriginKind.Host;
        var targetHandle = towardMstcp ? packet.Metadata.AdapterHandle : association.OriginAdapterHandle;

        var directionFlags = towardMstcp ? NdisApiAbi.PacketFlagOnReceive : NdisApiAbi.PacketFlagOnSend;
        var inPlace = CanRewriteInPlace(packet);
        var buffer = inPlace ? packet.NativeFrame.Buffer! : _framePool.Rent();
        var deferred = false;
        try
        {
            var frame = StageFrame(packet, buffer, inPlace, directionFlags, targetHandle);
            // Read-then-write: record the SYN-ACK sequence and advance the server sequence tracker
            // on the pre-rewrite bytes before the rewrite mutates them.
            TcpSequenceObservation.RecordServerSynAck(frame, packet.Layout, association);
            TcpSequenceObservation.TrackServerSequence(frame, packet.Layout, association);
            if (!PacketChecksums.TryRewriteTcpEndpoints(frame, packet.Layout, original.Remote.Address, original.Remote.Port, original.Local.Address, original.Local.Port))
            {
                return FailAssociationAndBlockAsync(association);
            }
            // The MAC swap makes the looped-back frame look inbound from the router for a host flow.
            // A forwarded flow's reversed frame is emitted on the origin adapter toward the client, and
            // its arrival MACs (this host -> client) are already correct.
            // ReSharper disable once ConvertIfStatementToSwitchStatement // A single conditional void call has no switch shape; the inspection fires on the opposite-condition guard below, and a bool switch would only restate the condition.
            if (towardMstcp) TcpFrameRewriter.SwapEthernetMacs(frame);
            // The forwarded direction must have a target handle before anything is queued: a null
            // origin handle would otherwise be committed to a lane and fail only at flush time.
            if (!towardMstcp && targetHandle == 0) return FailAssociationAndBlockAsync(association);
            buffer.CompleteFrame(frame.Length, directionFlags, targetHandle);
            deferred = TryDeferRedirectFrame(packet, buffer, rented: !inPlace, association, towardMstcp, targetHandle);
            if (deferred)
            {
                LogReverseFrameInjected(packet, association, "batched");
                return ValueTask.FromResult(TcpRedirectOutcome.Injected);
            }
            return InjectReverseFrameAsync(packet, association, buffer, towardMstcp, targetHandle, cancellationToken);
        }
        finally
        {
            ReleaseStage(buffer, inPlace, deferred);
        }
    }

    /// <summary>
    /// Sends the rewritten reverse frame and maps every failure to the established reverse-path
    /// posture: caller cancellation propagates untouched, a foreign OCE fails the association, any
    /// other injection failure surfaces to the client through <see cref="ClientResetInjector"/>
    /// before failing closed. Non-async so the per-packet reverse path never boxes a state
    /// machine; the cold failure tails run in their own async helpers.
    /// </summary>
#pragma warning disable RCS1229 // Deliberate non-async warm entry (hot-path.md → warm-path-dispatch.md, "No async state machines on the steady-state path"): the per-packet reverse path must not pay an async state machine; the cold failure tails run in their own async helpers.
    private ValueTask<TcpRedirectOutcome> InjectReverseFrameAsync(CapturedFlowPacket packet, TcpRedirectAssociation association, NdisPacketBuffer buffer, bool towardMstcp, nint targetHandle, CancellationToken cancellationToken)
#pragma warning restore RCS1229
    {
        try
        {
            _injector.Inject(buffer, towardMstcp, targetHandle, cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromException<TcpRedirectOutcome>(exception);
        }
        catch (OperationCanceledException exception)
        {
            return FailAssociationAndRethrowAsync(association, exception);
        }
        catch (Exception exception)
        {
            return HandleInjectionFailureAndBlockAsync(association, targetHandle, towardMstcp, exception);
        }

        LogReverseFrameInjected(packet, association, reason: null);
        return ValueTask.FromResult(TcpRedirectOutcome.Injected);
    }
}
