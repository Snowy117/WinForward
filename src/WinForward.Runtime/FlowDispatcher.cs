using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime.TcpRedirect;
using WinForward.Windows;

namespace WinForward.Runtime;

/// <summary>
/// The native capture metadata a reinjection executor needs to decide where a passed frame returns:
/// the NDISAPI direction flag, packet metadata flags, and the adapter handle it was captured on.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct PacketCaptureMetadata(uint DeviceFlags, nint AdapterHandle, uint Flags = 0)
{
    public bool IsOnSend => (DeviceFlags & NdisApiAbi.PacketFlagOnSend) != 0;
}

/// <summary>
/// The capture pump's native buffer holding a frame, valid until the pump reuses the slot in its
/// next batch read (the pump awaits each handler and runs its batch-completed callback before that
/// read, so the slot cannot be reused earlier). Carried alongside the lease so an in-place
/// reinjection can hand the pump's own buffer to the batched send: the pass executor and the TCP
/// redirect data legs rewrite it in place, and a redirect injection lane may retain it until that
/// iteration's flush. A consumer that needs the bytes past that window — a relay, or any reader
/// keeping the frame beyond its own call — must copy through the lease.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NativeFrameHandle(NdisPacketBuffer? Buffer);

[StructLayout(LayoutKind.Auto)]
public readonly record struct CapturedFlowPacket(
    PacketLease Lease,
    FlowContext Context,
    PacketCaptureMetadata Metadata = default,
    long PacketSequence = 0,
    long FlowGeneration = 0,
    NativeFrameHandle NativeFrame = default,
    PacketLayout Layout = default)
{
    /// <summary>
    /// A read-only view of the frame bytes for synchronous header inspection: the native capture
    /// buffer while the lease has not materialized (so bit-test paths like fragment detection
    /// never force a pooled managed copy), otherwise the lease's managed frame. Requires a
    /// non-null <see cref="Lease"/> (every dispatch entry rejects a leaseless packet first); the
    /// span must be consumed synchronously and must not escape the dispatch section.
    /// </summary>
    internal ReadOnlySpan<byte> InspectionSpan => NativeFrame.Buffer is { } buffer ? buffer.GetFrame() : Lease.Frame.Span;
}

/// <summary>
/// The shared entry guard for captured packets: rejects a packet whose <see cref="PacketLease"/>
/// is null. Every dispatch entry (dispatcher, TCP coordinator, executor) enforces the same
/// contract through this helper so the <see cref="ArgumentNullException.ParamName"/> points at
/// the member that is actually null — the packet itself is a struct and can never be null.
/// </summary>
internal static class CapturedFlowPacketGuards
{
    [DoesNotReturn]
#pragma warning disable MA0015, S3928 // The paramName deliberately names the null member (the packet struct is never itself null); both analyzers only accept declared parameter names, which would point diagnosis at a phantom "packet".
    public static void ThrowLeaseRequired() =>
        // ReSharper disable once NotResolvedInText // Deliberate member-path paramName: the centralized guard route (MA0015/S3928/CA2208 are scoped-suppressed for the same reason) names the null member, which ReSharper cannot resolve as a symbol.
        throw new ArgumentNullException("packet.Lease", "The captured packet requires a lease.");
#pragma warning restore MA0015, S3928
}

public interface ISelfTrafficGuard
{
    /// <summary>
    /// The full ownership check: the exact-tuple pair and the wildcard relay-socket pair. Claim-time
    /// only (a self-owned exact tuple never produces a flow-table state), so it may take the registry
    /// gate.
    /// </summary>
    bool IsOwned(FlowContext context);

    /// <summary>
    /// The wildcard half alone: a relay control socket registered as <c>(protocol, Any:port, remote)</c>
    /// before its SYN leaves the host. Loop prevention is fail-closed (traffic-policy-lifecycle.md), so
    /// this half stays on the warm entry; the implementation answers it without a process-wide lock.
    /// </summary>
    bool IsWildcardOwned(FlowContext context);
}

public interface IPacketActionExecutor
{
    ValueTask PassAsync(CapturedFlowPacket packet);
    ValueTask BlockAsync(CapturedFlowPacket packet);
    ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken);
}

public sealed class FlowDispatcher : IFlowAttributionHost
{
    /// <summary>The executor step a completed packet dispatches into; chosen by value so the
    /// completion path allocates no delegate or closure.</summary>
    private enum PacketAction
    {
        None,
        Pass,
        Block,
        Proxy,
    }

    private readonly PolicySnapshot _policy;
    private readonly IReadOnlyDictionary<string, Socks5Server> _servers;
    private readonly FlowTable _flows;
    private readonly ISelfTrafficGuard _selfTraffic;
    private readonly IPacketActionExecutor _executor;
    private readonly IProcessAttributor? _attributor;
    private readonly ISetupExecutor? _setupExecutor;
    private readonly ITcpReverseHandler? _reverseHandler;
    private readonly Func<CapturedFlowPacket, CancellationToken, ValueTask<TcpRedirectOutcome>>? _fragmentHandler;
    private readonly IRuntimeLogger _logger;
    private readonly bool _includeProcessPathInLogs;
    private readonly RuntimeLogThrottle _capacityBlockWarn = new(TimeSpan.FromSeconds(5));
    private readonly RuntimeLogThrottle _attributionMissWarn = new(TimeSpan.FromSeconds(5));

    public FlowDispatcher(ValidatedConfiguration configuration, ISelfTrafficGuard selfTraffic, IPacketActionExecutor executor, IProcessAttributor? attributor = null, int flowCapacity = 65_536, ITcpReverseHandler? reverseHandler = null, Func<CapturedFlowPacket, CancellationToken, ValueTask<TcpRedirectOutcome>>? fragmentHandler = null, IRuntimeLogger? logger = null, ActivityBucketClock? activityClock = null, NativeBufferPool? attributionPool = null, ISetupExecutor? setupExecutor = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(selfTraffic);
        ArgumentNullException.ThrowIfNull(executor);
        _policy = configuration.Policy;
        _servers = configuration.Servers;
        _flows = new FlowTable(flowCapacity, activityClock: activityClock);
        _selfTraffic = selfTraffic;
        _executor = executor;
        _attributor = attributor;
        _setupExecutor = setupExecutor;
        _reverseHandler = reverseHandler;
        _fragmentHandler = fragmentHandler;
        _logger = logger ?? NullRuntimeLogger.Instance;
        _includeProcessPathInLogs = configuration.IncludeProcessPathInLogs;
        // The deferred pipeline exists only where attribution can actually run: without a process
        // selector, an attributor, or an executor to run its work items, every eligible shape keeps
        // today's inline path byte-for-byte.
        if (attributionPool is not null && setupExecutor is not null && attributor is not null && _policy.RequiresProcessAttribution)
        {
            Attribution = new FlowAttributionPipeline(attributionPool, this, _logger);
        }
    }

    /// <summary>
    /// The deferred-attribution pipeline, or null when this dispatcher was composed without one
    /// (no process selector, no attributor, or no attribution pool and setup executor). Owned and
    /// disposed by the composition that supplied the pool.
    /// </summary>
    internal FlowAttributionPipeline? Attribution { get; }

    /// <summary>
    /// Removes flow decisions idle past <paramref name="idleTimeout"/> so the bounded flow table
    /// does not accumulate stale one-shot flows (design §7). The runtime calls this on a periodic
    /// sweep; active flows keep their decisions because observations touch them. Entries whose
    /// <paramref name="isHeld"/> predicate reports a live holder (e.g. a TCP redirect session
    /// still relaying, or a flow inside its post-teardown grace window) are skipped with their
    /// activity timestamp untouched, so they expire at their original idle point once the hold
    /// lapses.
    /// </summary>
    public int RemoveExpiredFlows(DateTimeOffset now, TimeSpan idleTimeout, Func<FlowKey, bool>? isHeld = null) => _flows.RemoveExpired(now, idleTimeout, isHeld);

    /// <summary>The number of flow decisions currently cached in the flow table (heartbeat diagnostics).</summary>
    public int FlowCount => _flows.Count;

    /// <summary>The flow table's fixed capacity (heartbeat diagnostics).</summary>
    public int FlowCapacity => _flows.Capacity;

    /// <summary>
    /// Dispatches a classified flow packet. The steady-state shape (no self-traffic wildcard, flow
    /// already resolved, pass, block, or a proxy decision that resolves inline to a known server)
    /// runs entirely synchronously on this non-async entry so the per-packet path allocates
    /// nothing: the executor call is returned directly and awaited exactly once by the caller.
    /// Every other shape — trace logging, a packet the wired reverse handler's diversion
    /// predicate claims (X1: TCP with a live listener source port), a wildcard self-traffic tuple,
    /// reverse UDP responses, new flows needing attribution (where the full self-traffic check
    /// runs), and proxy decisions that cannot resolve inline — falls into
    /// <see cref="DispatchSlowAsync"/>, which keeps the full state machine and all diagnostic
    /// logging.
    /// </summary>
    public ValueTask DispatchAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract // Deliberate fail-closed capture-boundary guard: Lease is declared non-nullable, but a default CapturedFlowPacket reaches runtime entries with a null lease; CapturedFlowPacketGuards.ThrowLeaseRequired reports the null member (quality-guidelines.md).
        if (packet.Lease is null) CapturedFlowPacketGuards.ThrowLeaseRequired();

        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) return DispatchSlowAsync(packet, cancellationToken);
        // X1: only packets the reverse handler itself claims can be reverse candidates divert;
        // a miss falls through here, and when the flow table also misses, the slow path still
        // runs the full handler — so tombstone stragglers keep their grace-drop behavior.
        if (_reverseHandler is not null && _reverseHandler.WantsPacket(packet)) return DispatchSlowAsync(packet, cancellationToken);
        // ReSharper disable once DuplicatedSequentialIfBodies // Warm-path bypass enumeration: the trace-only bypass (above) and each lane below (X1 reverse claim, self-traffic wildcard ownership, unresolved flow) is a distinct documented reason; merging couples unrelated predicates into one >150-char guard.
        if (_selfTraffic.IsWildcardOwned(packet.Context)) return DispatchSlowAsync(packet, cancellationToken);
        if (!_flows.TryResolveWarm(packet.Context.Key, out var existing)) return DispatchSlowAsync(packet, cancellationToken);

        var decision = existing.Decision;
        if (decision.Action == FlowAction.Proxy)
        {
            // Proxy is the main-path action (hot-path contract 3), so a decision that resolves
            // inline to a known server stays on the synchronous warm shape: the executor's
            // ValueTask is returned directly, allocating nothing when it completes synchronously.
            // An unresolved server name and the UDP reverse-response special case handled by
            // DispatchSlowAsync keep their slow-path behavior.
            if (decision.ProxyServerName is null || !_servers.TryGetValue(decision.ProxyServerName, out var server)) return DispatchSlowAsync(packet, cancellationToken);
            if (packet.Context.Key.Protocol == TransportProtocol.Udp && existing.Key.IsReverseOf(packet.Context.Key)) return DispatchSlowAsync(packet, cancellationToken);
            packet = packet with { FlowGeneration = existing.Generation };
            // ReSharper disable once ConvertIfStatementToReturnStatement // The condition completes the lease (side effect + state transition); folding it into a conditional expression hides the "already consumed" early exit (B1 disposition).
            if (!packet.Lease.TryComplete(PacketDisposition.ProxyConsumed)) return ValueTask.CompletedTask;
            return _executor.ProxyAsync(packet, server, cancellationToken);
        }

        packet = packet with { FlowGeneration = existing.Generation };
        var disposition = decision.Action == FlowAction.Pass ? PacketDisposition.Pass : PacketDisposition.Block;
        if (!packet.Lease.TryComplete(disposition)) return ValueTask.CompletedTask;
        return decision.Action == FlowAction.Pass
            ? _executor.PassAsync(packet)
            : _executor.BlockAsync(packet);
    }

    private async ValueTask DispatchSlowAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract // Deliberate fail-closed capture-boundary guard: Lease is declared non-nullable, but a default CapturedFlowPacket reaches runtime entries with a null lease; CapturedFlowPacketGuards.ThrowLeaseRequired reports the null member (quality-guidelines.md).
        if (packet.Lease is null) CapturedFlowPacketGuards.ThrowLeaseRequired();
        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacketStage(RuntimeLogLevel.Trace, "packet.classified", packet, new RuntimeLogField("kind", "flow"));
        if (await TryHandleSelfTrafficAsync(packet, cancellationToken).ConfigureAwait(false)) return;

        // A packet on an active TCP redirect leg (a port matches a proxy listener port) must be
        // reversed back to the original server:client tuple before the Windows stack sees it. This
        // runs before flow lookup and policy so a reverse packet is never re-evaluated as a new
        // client flow. Gated on TCP only (H1/M5): the reverse handler keys on numeric port alone,
        // so a UDP datagram whose port collides with a TCP listener port must never reach it.
        if (await TryHandleReverseAsync(packet, cancellationToken).ConfigureAwait(false)) return;

        if (_flows.TryResolve(packet.Context.Key, out var existing) && existing is not null)
        {
            packet = packet with { FlowGeneration = existing.Generation };
            if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacketStage(RuntimeLogLevel.Trace, "packet.flowResolved", packet, new RuntimeLogField("existing", Value: true));
            // A UDP proxy response (server -> client on an actively proxied flow) is injected by
            // UdpResponseReinjector toward the local stack; when the capture path observes it again
            // it must be delivered to the client, not re-proxied back to the relay. Detect by
            // direction: the stored flow key is (client:port -> server:port), a response is the
            // reverse. TCP reverse packets are handled by the reverse hook before this point.
            if (existing.Decision.Action == FlowAction.Proxy && packet.Context.Key.Protocol == TransportProtocol.Udp &&
                existing.Key.IsReverseOf(packet.Context.Key))
            {
                await CompleteAsync(packet, PacketDisposition.Pass, PacketAction.Pass, server: null, cancellationToken).ConfigureAwait(false);
                return;
            }
            await ExecuteDecisionAsync(packet, existing.Decision, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await TryDeferAttributionAsync(packet, cancellationToken).ConfigureAwait(false)) return;

        var context = packet.Context;
        context = await AttributeProcessAsync(context, cancellationToken).ConfigureAwait(false);

        if (!_flows.TryClaimResolved(context.Key, () => EvaluateNewFlow(context), out var claimed) || claimed is null)
        {
            LogCapacityBlock(context);
            if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacketStage(RuntimeLogLevel.Trace, "packet.flowResolved", packet, new RuntimeLogField("outcome", "capacity"));
            await CompleteAsync(packet, PacketDisposition.Block, PacketAction.Block, server: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        packet = packet with { Context = context, FlowGeneration = claimed.Generation };
        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacketStage(RuntimeLogLevel.Trace, "packet.flowResolved", packet, new RuntimeLogField("existing", Value: false));
        LogFlowCreated(packet.Context, packet.FlowGeneration, claimed.Decision);
        await ExecuteDecisionAsync(packet, claimed.Decision, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> TryHandleSelfTrafficAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        if (!_selfTraffic.IsOwned(packet.Context)) return false;
        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacketStage(RuntimeLogLevel.Trace, "packet.selfTraffic", packet, new RuntimeLogField("outcome", "pass"));
        await CompleteAsync(packet, PacketDisposition.Pass, PacketAction.Pass, server: null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<bool> TryHandleReverseAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        if (_reverseHandler is null || packet.Context.Key.Protocol != TransportProtocol.Tcp) return false;
        var outcome = await _reverseHandler.HandleReverseIfApplicableAsync(packet, cancellationToken).ConfigureAwait(false);
        if (outcome == TcpRedirectOutcome.NotRelevant) return false;
        // Dropped is a TIME_WAIT-grace tombstone hit consumed by the proxy layer, like Injected.
        // Block is reserved for outcome Blocked: routing a grace drop through BlockAsync would
        // mislabel it as a policy drop (packet.dropped reason=policy) in the trace.
        var disposition = outcome == TcpRedirectOutcome.Blocked ? PacketDisposition.Block : PacketDisposition.ProxyConsumed;
        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacketStage(RuntimeLogLevel.Trace, "packet.reverseHandled", packet, new RuntimeLogField("outcome", outcome));
        await CompleteAsync(packet, disposition, disposition == PacketDisposition.Block ? PacketAction.Block : PacketAction.None, server: null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Routes an eligible new-flow miss into the deferred pipeline. Returns true when the packet is
    /// settled — retained for a worker verdict, or blocked fail-closed — and false when the caller
    /// must continue its inline path.
    /// <para>
    /// Every ineligible shape (no process selector, a forwarded origin, a context that already
    /// carries a process) is refused here before the pipeline is touched, so those flows keep
    /// today's path byte-for-byte instead of paying a worker round-trip for nothing.
    /// </para>
    /// </summary>
    private async ValueTask<bool> TryDeferAttributionAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        if (Attribution is null || !ShouldAttribute(packet.Context)) return false;
        switch (Attribution.Admit(packet))
        {
            case AttributionAdmission.Deferred:
                // The retained copy is the pipeline's; releasing the capture lease on the admitting
                // pump thread is what keeps the thread-local recycle cache correct.
                _ = packet.Lease.TryComplete(PacketDisposition.Deferred);
                return true;
            case AttributionAdmission.Inline:
                // Admission found the flow while it held the pending gate; an expiry between the
                // two resolutions is the only way past this, and the inline path recovers it.
                if (_flows.TryResolve(packet.Context.Key, out var admitted) && admitted is not null)
                {
                    await ExecuteDecisionAsync(packet with { FlowGeneration = admitted.Generation }, admitted.Decision, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                return false;
            case AttributionAdmission.BlockedFailed
                or AttributionAdmission.BlockedSealed
                or AttributionAdmission.BlockedPending
                or AttributionAdmission.BlockedFlowFull
                or AttributionAdmission.BlockedCooldown:
                return await BlockFailClosedAsync(packet, cancellationToken).ConfigureAwait(false);
        }

        // Unreachable for every current outcome; a member added to the enum settles fail-closed here
        // rather than leaving the packet neither retained nor blocked.
        return await BlockFailClosedAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The refusal arm: the packet is blocked fail-closed and reported as settled.</summary>
    private async ValueTask<bool> BlockFailClosedAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        await CompleteAsync(packet, PacketDisposition.Block, PacketAction.Block, server: null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<FlowContext> AttributeProcessAsync(FlowContext context, CancellationToken cancellationToken)
    {
        if (!ShouldAttribute(context)) return context;
        var identity = await _attributor!.FindAsync(context.Key, cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            LogAttributionMiss(context);
            return context;
        }
        return context with { Process = new ProcessMetadata(identity.Value.Name, identity.Value.FullPath) };
    }

    /// <summary>
    /// The single eligibility predicate for process attribution, shared by the inline path and the
    /// deferred pipeline's admission so the two cannot drift. A miss still means "no process
    /// match": evaluation continues without one, exactly as before.
    /// </summary>
    private bool ShouldAttribute(FlowContext context) =>
        _policy.RequiresProcessAttribution
        && _attributor is not null
        && context.Process is null
        && context.Key.Origin == FlowOriginKind.Host;

    FlowTable IFlowAttributionHost.Flows => _flows;

    ISetupExecutor IFlowAttributionHost.SetupExecutor =>
        _setupExecutor ?? throw new InvalidOperationException("The deferred-attribution pipeline requires a setup executor.");

    IPacketActionExecutor IFlowAttributionHost.Executor => _executor;

    IReadOnlyDictionary<string, Socks5Server> IFlowAttributionHost.Servers => _servers;

    PolicySnapshot IFlowAttributionHost.Policy => _policy;

    ValueTask<FlowContext> IFlowAttributionHost.AttributeAsync(FlowContext context, CancellationToken cancellationToken) =>
        AttributeProcessAsync(context, cancellationToken);

    void IFlowAttributionHost.LogCapacityBlock(FlowContext context) => LogCapacityBlock(context);

    void IFlowAttributionHost.LogFlowCreated(FlowContext context, long generation, FlowDecision decision) =>
        LogFlowCreated(context, generation, decision);

    /// <summary>
    /// The capacity-gate block warn (<c>flow.capacity-block</c>): the flow table is full, so a new
    /// flow is blocked fail-closed; the trace-only record does not surface sustained capacity
    /// exhaustion to operators. Throttled to one line per window (the block itself repeats per
    /// packet); the counter aggregates every occurrence. Logging only: the Block disposition is
    /// unchanged.
    /// </summary>
    private void LogCapacityBlock(FlowContext context)
    {
        RuntimeCounters.Shared.Increment(RuntimeCounters.FlowCapacityBlock);
        if (!_capacityBlockWarn.ShouldEmit() || !_logger.IsEnabled(RuntimeLogLevel.Warn)) return;
        _logger.Event(RuntimeLogLevel.Warn, "flow.capacity-block",
            new("protocol", context.Key.Protocol),
            new("origin", context.Key.Origin),
            new("source", context.Key.Local),
            new("destination", context.Key.Remote),
            new("tableSize", _flows.Count),
            new("capacity", _flows.Capacity));
    }

    private void LogFlowCreated(FlowContext context, long generation, FlowDecision decision)
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Debug)) return;
        var fields = new RuntimeLogField[10];
        fields[0] = new("flow", generation == 0 ? null : generation);
        fields[1] = new("protocol", context.Key.Protocol);
        fields[2] = new("origin", context.Key.Origin);
        fields[3] = new("source", context.Key.Local);
        fields[4] = new("destination", context.Key.Remote);
        fields[5] = new("process", context.ProcessName);
        fields[6] = new("processPath", _includeProcessPathInLogs ? context.ProcessPath : null);
        fields[7] = new("action", decision.Action);
        fields[8] = new("rule", decision.RuleIndex);
        fields[9] = new("proxy", decision.ProxyServerName);
        _logger.Event(RuntimeLogLevel.Debug, "flow.created", fields);
    }

    /// <summary>
    /// The attribution-miss warn (<c>flow.attribution-miss</c>): process attribution returned no
    /// owner for a host flow. The production attributor already retried once internally before
    /// returning null, so the miss is always post-retry (<c>afterRetry=true</c>). Throttled to one
    /// line per window; the counter aggregates every occurrence. Logging only: an unresolved owner
    /// still matches no process rule and evaluation continues unchanged.
    /// </summary>
    private void LogAttributionMiss(FlowContext context)
    {
        RuntimeCounters.Shared.Increment(RuntimeCounters.AttributionMiss);
        if (!_attributionMissWarn.ShouldEmit() || !_logger.IsEnabled(RuntimeLogLevel.Warn)) return;
        _logger.Event(RuntimeLogLevel.Warn, "flow.attribution-miss",
            new("protocol", context.Key.Protocol),
            new("local", context.Key.Local),
            new("remote", context.Key.Remote),
            new("afterRetry", Value: true));
    }

    /// <summary>
    /// Handles a frame that cannot be classified into a TCP/UDP flow (non-IP, fragmented, malformed,
    /// or a non-TCP/UDP protocol). Such frames can never enter a proxy relay, so policy does not
    /// apply to them: they are always passed. Blocking them blackholes ARP, ICMP/ND, and PMTUD and
    /// severs the very connectivity proxied flows depend on. The frame is not cached in the flow
    /// table because these frames have no logical flow.
    /// </summary>
    public async ValueTask DispatchNonFlowAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract // Deliberate fail-closed capture-boundary guard: Lease is declared non-nullable, but a default CapturedFlowPacket reaches runtime entries with a null lease; CapturedFlowPacketGuards.ThrowLeaseRequired reports the null member (quality-guidelines.md).
        if (packet.Lease is null) CapturedFlowPacketGuards.ThrowLeaseRequired();
        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacketStage(RuntimeLogLevel.Trace, "packet.classified", packet, new RuntimeLogField("kind", "nonFlow"));
        if (_selfTraffic.IsOwned(packet.Context))
        {
            if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacketStage(RuntimeLogLevel.Trace, "packet.selfTraffic", packet, new RuntimeLogField("outcome", "pass"));
            await CompleteAsync(packet, PacketDisposition.Pass, PacketAction.Pass, server: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        // An IP fragment cannot be classified as a flow, but its address pair may belong to an
        // active TCP redirect: such fragments must never pass toward the real server (S1). The
        // check is a bounded bit test over the raw header — the non-fragment non-flow majority
        // (ARP, ND, L2) pays only an ether-type compare.
        if (await TryHandleFragmentAsync(packet, cancellationToken).ConfigureAwait(false)) return;

        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacketStage(RuntimeLogLevel.Trace, "packet.action", packet, new RuntimeLogField("action", FlowAction.Pass), new RuntimeLogField("rule", Value: null), new RuntimeLogField("proxy", Value: null), new RuntimeLogField("reason", "nonFlow"));
        await CompleteAsync(packet, PacketDisposition.Pass, PacketAction.Pass, server: null, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> TryHandleFragmentAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        if (_fragmentHandler is null || !IPFragment.IsFragment(packet.InspectionSpan)) return false;
        var outcome = await _fragmentHandler(packet, cancellationToken).ConfigureAwait(false);
        if (outcome == TcpRedirectOutcome.NotRelevant) return false;
        // Dropped is an attributed fragment consumed by the proxy layer, like a grace drop;
        // Blocked (a disposed-coordinator race) keeps the policy-drop executor path.
        var disposition = outcome == TcpRedirectOutcome.Blocked ? PacketDisposition.Block : PacketDisposition.ProxyConsumed;
        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacketStage(RuntimeLogLevel.Trace, "packet.fragmentHandled", packet, new RuntimeLogField("outcome", outcome));
        await CompleteAsync(packet, disposition, disposition == PacketDisposition.Block ? PacketAction.Block : PacketAction.None, server: null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private FlowDecision EvaluateNewFlow(FlowContext context) =>
        context.Key.Origin == FlowOriginKind.Forwarded ? _policy.EvaluateForwarded(context) : _policy.EvaluateHost(context);

    private async ValueTask ExecuteDecisionAsync(CapturedFlowPacket packet, FlowDecision decision, CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacketStage(RuntimeLogLevel.Trace, "packet.action", packet, new RuntimeLogField("action", decision.Action), new RuntimeLogField("rule", decision.RuleIndex), new RuntimeLogField("proxy", decision.ProxyServerName));
        switch (decision.Action)
        {
            case FlowAction.Pass:
                await CompleteAsync(packet, PacketDisposition.Pass, PacketAction.Pass, server: null, cancellationToken).ConfigureAwait(false);
                return;
            case FlowAction.Block:
                await CompleteAsync(packet, PacketDisposition.Block, PacketAction.Block, server: null, cancellationToken).ConfigureAwait(false);
                return;
            case FlowAction.Proxy when decision.ProxyServerName is not null && _servers.TryGetValue(decision.ProxyServerName, out var server):
                await CompleteAsync(packet, PacketDisposition.ProxyConsumed, PacketAction.Proxy, server, cancellationToken).ConfigureAwait(false);
                return;
            default:
                await CompleteAsync(packet, PacketDisposition.Block, PacketAction.Block, server: null, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async ValueTask CompleteAsync(CapturedFlowPacket packet, PacketDisposition disposition, PacketAction action, Socks5Server? server, CancellationToken cancellationToken)
    {
        if (!packet.Lease.TryComplete(disposition)) return;
        switch (action)
        {
            case PacketAction.Pass:
                await _executor.PassAsync(packet).ConfigureAwait(false);
                break;
            case PacketAction.Block:
                await _executor.BlockAsync(packet).ConfigureAwait(false);
                break;
            case PacketAction.Proxy when server is not null:
                await _executor.ProxyAsync(packet, server, cancellationToken).ConfigureAwait(false);
                break;
            case PacketAction.None:
                // The caller completed the lease with its own disposition and there is no executor
                // action for it (reverse/fragment handled paths pass None); nothing to dispatch.
                break;
            default:
                // PacketAction is a closed private enum: a value outside its members means a caller
                // asked for an action the dispatcher cannot complete — fail loudly, not silently.
                throw new InvalidOperationException($"Unhandled packet action '{action}'.");
        }
        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacketStage(RuntimeLogLevel.Trace, "packet.completed", packet, new RuntimeLogField("disposition", disposition));
    }

    private void LogPacketStage(RuntimeLogLevel level, string eventName, CapturedFlowPacket packet, params RuntimeLogField[] fields)
    {
        if (!_logger.IsEnabled(level)) return;
        var allFields = new RuntimeLogField[fields.Length + 8];
        allFields[0] = new("packet", packet.PacketSequence == 0 ? null : packet.PacketSequence);
        allFields[1] = new("flow", packet.FlowGeneration == 0 ? null : packet.FlowGeneration);
        fields.CopyTo(allFields, 2);
        allFields[fields.Length + 2] = new("protocol", packet.Context.Key.Protocol);
        allFields[fields.Length + 3] = new("origin", packet.Context.Key.Origin);
        allFields[fields.Length + 4] = new("source", packet.Context.Key.Local);
        allFields[fields.Length + 5] = new("destination", packet.Context.Key.Remote);
        allFields[fields.Length + 6] = new("process", packet.Context.ProcessName);
        allFields[fields.Length + 7] = new("processPath", _includeProcessPathInLogs ? packet.Context.ProcessPath : null);
        _logger.Event(level, eventName, allFields);
    }
}
