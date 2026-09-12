using System.Diagnostics;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Runtime.Capture;

/// <summary>
/// Executes pass/block/proxy packet dispositions. A pass accumulates its frame for reinjection
/// exactly once in its captured direction through the <see cref="IPacketReinjector"/>: unmodified
/// frames are deferred in their capture buffer, materialized frames in a pooled native copy, and
/// <see cref="FlushPendingPasses"/> sends each (adapter, direction) lane as one batched request at
/// the end of the pump iteration that accumulated it (the pump's batch-completed callback; see
/// design 08-30-batched-ioctls D2 — every <see cref="PassAsync"/> caller lives inside the pump's
/// serialized batch-loop chain). The lane table is sized from the capture scope:
/// <see cref="RetireLanesExcept"/> rebuilds it at <c>2 × scope-count</c> lanes when a scope
/// installs (task 09-12-lane-table-scope-sizing), so in-scope adapters can never overflow to
/// immediate single sends and refresh-churned handles can never fill the table with dead keys —
/// the rebuild subsumes lane retirement because the fresh table only ever holds live-scope lanes.
/// A block consumes the frame without reinjection. A proxy decision
/// routes TCP packets through the <see cref="TcpProxyCoordinator"/> and UDP datagrams through the
/// <see cref="UdpProxyCoordinator"/> when one is configured; if no matching coordinator is provided
/// the flow fails closed with a rate-limited structured log.
/// </summary>
public sealed class NdisPacketActionExecutor : IPacketActionExecutor
{
    /// <summary>Shared rate-limit window for this executor's recoverable-fault warnings.</summary>
    private static readonly TimeSpan RateLimitedWarnInterval = TimeSpan.FromSeconds(5);

    // One lane per (adapter handle, direction). The table starts at a pre-install capacity (the
    // window before the first scope install, when the adapter count is still unknown) and is
    // rebuilt at 2 × scope-count lanes on every scope install — each in-scope adapter needs at
    // most a send and a receive lane, so in-scope keys can never overflow. The overflow path
    // (immediate single send, counted and warned, never silent) remains as a defensive backstop,
    // reachable only pre-install or while interception is paused with an empty scope (a stray
    // pass then still goes out exactly once).
    private const int PreInstallLaneCapacity = 8;
    private const int InitialLaneFrames = 8;

    private readonly IPacketReinjector _reinjector;
    private readonly TcpProxyCoordinator? _tcpProxy;
    private readonly UdpProxyCoordinator? _udpProxy;
    private readonly IRuntimeLogger _logger;
    private readonly NdisPacketBufferPool _bufferPool;
    private readonly Lock _pendingLaneLock = new();
    private PendingPassLane?[] _pendingLanes = new PendingPassLane?[PreInstallLaneCapacity];
    private long _lastProxyUnavailableLogTicks;
    private long _lastUdpFailureLogTicks;
    private long _lastLaneOverflowLogTicks;
    private long _lastLaneRetireLogTicks;
    private long _immediateSendLaneOverflowCount;

    public NdisPacketActionExecutor(IPacketReinjector reinjector, IRuntimeLogger? logger = null, TcpProxyCoordinator? tcpProxy = null, UdpProxyCoordinator? udpProxy = null, NdisPacketBufferPool? bufferPool = null)
    {
        ArgumentNullException.ThrowIfNull(reinjector);
        _reinjector = reinjector;
        _tcpProxy = tcpProxy;
        _udpProxy = udpProxy;
        _logger = logger ?? NullRuntimeLogger.Instance;
        _bufferPool = bufferPool ?? NdisPacketBufferPool.Shared;
    }

    public ValueTask PassAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        if (packet.Lease is null) CapturedFlowPacketGuards.ThrowLeaseRequired();
        var metadata = packet.Metadata;
        if (packet.NativeFrame.Buffer is { } captureBuffer && !packet.Lease.IsMaterialized)
        {
            // Unmodified frame still sitting in its capture buffer: reinject it in place (only the
            // enumeration handle is retargeted; direction, length, flags, and payload stay as
            // captured). The send is deferred to the iteration-end flush, which the pump runs
            // before its next batch read — batch slots stay stable for the whole iteration, so no
            // managed copy ever happens.
            captureBuffer.PrepareForReinjection(metadata.AdapterHandle);
            AppendPass(metadata.AdapterHandle, metadata.IsOnSend, captureBuffer, rented: false);
        }
        else
        {
            // The rented native buffer copies the frame synchronously here, so the lease's pooled
            // managed array can return when ProcessAsync completes; the flush owns the native
            // buffer from this point and returns it exactly once after sending.
            var buffer = _bufferPool.Rent();
            buffer.SetFrame(packet.Lease.Frame.Span, metadata.DeviceFlags, metadata.AdapterHandle, metadata.Flags);
            AppendPass(metadata.AdapterHandle, metadata.IsOnSend, buffer, rented: true);
        }
        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacket("packet.reinjected", packet, new RuntimeLogField("target", metadata.IsOnSend ? "adapter" : "mstcp"));
        return ValueTask.CompletedTask;
    }

    private void AppendPass(nint adapterHandle, bool isOnSend, NdisPacketBuffer buffer, bool rented)
    {
        var lane = TryGetOrAddPendingLane(adapterHandle, isOnSend);
        if (lane is null)
        {
            // More concurrent (adapter, direction) lanes than the current table holds — possible
            // only pre-install or while interception is paused with an empty scope: send now so
            // the frame still goes out exactly once instead of being dropped from batching. The
            // degradation is observable by design (P0-1): counted for tests/telemetry, warned
            // rate-limited for operators.
            Interlocked.Increment(ref _immediateSendLaneOverflowCount);
            if (ShouldWarn(ref _lastLaneOverflowLogTicks))
                _logger.Warn($"Pass batching is degraded to immediate single sends because more than {Volatile.Read(ref _pendingLanes).Length} concurrent (adapter, direction) lanes are active.");
            if (isOnSend) _reinjector.SendToAdapter(adapterHandle, buffer);
            else _reinjector.SendToMstcp(adapterHandle, buffer);
            if (rented) buffer.Dispose();
            return;
        }
        var count = lane.Count;
        if (count == lane.Buffers.Length)
        {
            Array.Resize(ref lane.Buffers, checked(count * 2));
            Array.Resize(ref lane.Rented, checked(count * 2));
        }
        lane.Buffers[count] = buffer;
        lane.Rented[count] = rented;
        lane.Count = count + 1;
    }

    /// <summary>
    /// Resolves the accumulation lane for one (adapter handle, direction). The scan is lock-free
    /// once a lane exists (lanes are published fully constructed with volatile semantics and are
    /// replaced only between generations by <see cref="RetireLanesExcept"/>'s table rebuild, under
    /// this same creation lock); first sight of a key creates its lane under the creation lock. A
    /// lane is only ever touched by its adapter's pump chain — every <see cref="PassAsync"/>
    /// caller runs inside that pump's serialized batch loop within one generation, and flushes are
    /// issued per adapter — so appends and flushes need no per-lane lock. The table reference is
    /// snapshotted once per call: the rebuild swaps the array between generations, and a caller
    /// may safely observe either the old or the new table, never a torn mix.
    /// </summary>
    private PendingPassLane? TryGetOrAddPendingLane(nint adapterHandle, bool isOnSend)
    {
        var lanes = Volatile.Read(ref _pendingLanes);
        for (var index = 0; index < lanes.Length; index++)
        {
            if (MatchLane(lanes, index, adapterHandle, isOnSend) is { } existing) return existing;
        }
        lock (_pendingLaneLock)
        {
            // Re-read the table inside the lock: a rebuild may have swapped it while this caller
            // was waiting, and lane creation must land in the current table.
            lanes = Volatile.Read(ref _pendingLanes);
            var freeIndex = -1;
            for (var index = 0; index < lanes.Length; index++)
            {
                if (MatchLane(lanes, index, adapterHandle, isOnSend) is { } existing) return existing;
                if (freeIndex < 0 && Volatile.Read(ref lanes[index]) is null) freeIndex = index;
            }
            if (freeIndex < 0) return null;
            var lane = new PendingPassLane(adapterHandle, isOnSend, InitialLaneFrames);
            Volatile.Write(ref lanes[freeIndex], lane);
            return lane;
        }
    }

    private static PendingPassLane? MatchLane(PendingPassLane?[] lanes, int index, nint adapterHandle, bool isOnSend) =>
        Volatile.Read(ref lanes[index]) is { } lane && lane.AdapterHandle == adapterHandle && lane.ToAdapter == isOnSend ? lane : null;

    /// <summary>
    /// Sends every pass frame accumulated for one adapter, one batched reinjector call per
    /// direction lane in lane-creation order, preserving append (capture) order within each lane.
    /// Rented pooled buffers are returned exactly once in a <c>finally</c>, so a failed batch
    /// still releases them; in-place capture buffers are never returned (the pump owns them).
    /// Called by the pump's batch-completed callback once per iteration and once on loop exit.
    /// </summary>
    public void FlushPendingPasses(nint adapterHandle)
    {
        var lanes = Volatile.Read(ref _pendingLanes);
        for (var index = 0; index < lanes.Length; index++)
        {
            if (Volatile.Read(ref lanes[index]) is not { } lane) continue;
            if (lane.AdapterHandle != adapterHandle) continue;
            FlushLane(lane);
        }
    }

    private void FlushLane(PendingPassLane lane)
    {
        var count = lane.Count;
        lane.Count = 0;
        if (count == 0) return;
        try
        {
            if (lane.ToAdapter) _reinjector.SendPacketsToAdapter(lane.AdapterHandle, lane.Buffers, count);
            else _reinjector.SendPacketsToMstcp(lane.AdapterHandle, lane.Buffers, count);
        }
        finally
        {
            ReleaseLaneBuffers(lane, count);
        }
    }

    /// <summary>
    /// Rebuilds the lane table at <c>2 × <paramref name="activeAdapterHandles"/>.Length</c> slots
    /// — the scope-installed callback's between-generations contract (task
    /// 09-12-lane-table-scope-sizing), which sizes the table from the installed capture scope so
    /// an in-scope adapter can never overflow to immediate single sends, and subsumes lane
    /// retirement: the fresh table only ever holds live-scope keys. Lanes whose adapter handle is
    /// in scope are MIGRATED — the lane object moves with its identity and any pending frames
    /// intact. Migration is mandatory, not an optimization:
    /// <see cref="LayeredCaptureRunner"/> starts the new generation's run before invoking the
    /// scope-installed callback, so the new generation's pumps may already have appended passes
    /// to the old table; lane creation is serialized by this same lock, so no lane can be created
    /// in the old table while the rebuild runs, and lock-free appends only touch lane objects
    /// that migration preserves. Dropping in-scope lanes instead of migrating them would silently
    /// stranded those pending frames. A lane absent from
    /// <paramref name="activeAdapterHandles"/> is retired with today's breach semantics; an empty
    /// span therefore retires every lane and installs a zero-capacity table (interception paused;
    /// a stray pass then takes the immediate single-send backstop). Precondition: no pump for a
    /// retired handle may still be running —
    /// the capture runner invokes this from its scope-installed callback, strictly after the
    /// outgoing generation's run task (including its loop-exit flush) has completed and before
    /// the next generation starts. A retired lane that still holds frames indicates a breach of
    /// that contract: the frames are dropped fail-closed (their adapter is gone) with a
    /// rate-limited warn, and their rented buffers are still returned exactly once. Driver
    /// handle-value reuse is safe: a numerically reused handle is indistinguishable from — and
    /// behaviorally equivalent to — the old lane key.
    /// </summary>
    internal void RetireLanesExcept(ReadOnlySpan<nint> activeAdapterHandles)
    {
        lock (_pendingLaneLock)
        {
            // Holding the lock pins the current table: the rebuild is the only writer of the
            // field, and lane creation re-reads the field under this same lock.
            var current = _pendingLanes;
            var next = new PendingPassLane?[activeAdapterHandles.Length * 2];
            var nextIndex = 0;
            for (var index = 0; index < current.Length; index++)
            {
                if (Volatile.Read(ref current[index]) is not { } lane) continue;
                // At most two lanes exist per adapter handle (one per direction) by construction,
                // so in-scope migration can never overflow a 2 × scope-count table; the bound
                // check stays as a defensive guard for an impossible invariant breach.
                if (activeAdapterHandles.Contains(lane.AdapterHandle) && nextIndex < next.Length)
                {
                    next[nextIndex] = lane;
                    nextIndex++;
                    continue;
                }
                var stale = lane.Count;
                lane.Count = 0;
                if (stale > 0)
                {
                    if (ShouldWarn(ref _lastLaneRetireLogTicks))
                        _logger.Warn($"A pass lane retired for adapter 0x{lane.AdapterHandle:X} still held {stale} frame(s); the frames are dropped and their rented buffers returned because the iteration-end flush contract was breached.");
                    ReleaseLaneBuffers(lane, stale);
                }
            }
            Volatile.Write(ref _pendingLanes, next);
        }
    }

    /// <summary>Returns exactly the first <paramref name="count"/> lane entries: pooled copies back to the pool, in-place capture buffers untouched.</summary>
    private static void ReleaseLaneBuffers(PendingPassLane lane, int count)
    {
        for (var index = 0; index < count; index++)
        {
            if (lane.Rented[index]) lane.Buffers[index].Dispose();
            lane.Buffers[index] = null!;
        }
    }

    /// <summary>Telemetry/diagnostic surface: pass frames currently waiting for a flush.</summary>
    internal int PendingPassCount
    {
        get
        {
            var lanes = Volatile.Read(ref _pendingLanes);
            var total = 0;
            for (var index = 0; index < lanes.Length; index++)
            {
                if (Volatile.Read(ref lanes[index]) is { } lane) total += lane.Count;
            }
            return total;
        }
    }

    /// <summary>
    /// Telemetry/diagnostic surface: pass frames forced onto the immediate single-send path
    /// because more concurrent (adapter, direction) lanes than the current table holds were
    /// active — reachable only before the first scope install (pre-install capacity) or while
    /// interception is paused with an empty scope; a scope-sized table never overflows for
    /// in-scope adapters.
    /// </summary>
    internal long ImmediateSendLaneOverflowCount => Volatile.Read(ref _immediateSendLaneOverflowCount);

    /// <summary>
    /// Debug-only guard for the batching contract: pins that one adapter's lanes are fully
    /// drained (a flush ran and nothing new accumulated), so a <see cref="PassAsync"/> caller
    /// escaping the pump's batch-loop chain — whose frames would never be sent — is caught in
    /// debug builds and tests instead of silently stranding frames.
    /// </summary>
    [Conditional("DEBUG")]
    internal void DebugAssertNoPendingPasses(nint adapterHandle)
    {
        var lanes = Volatile.Read(ref _pendingLanes);
        for (var index = 0; index < lanes.Length; index++)
        {
            if (MatchLane(lanes, index, adapterHandle, isOnSend: false) is { } mstcpLane) Debug.Assert(mstcpLane.Count == 0, $"Pass frames for adapter 0x{adapterHandle:X} are still pending outside a pump iteration; every iteration must flush (batched reinjection contract).");
            if (MatchLane(lanes, index, adapterHandle, isOnSend: true) is { } adapterLane) Debug.Assert(adapterLane.Count == 0, $"Pass frames for adapter 0x{adapterHandle:X} are still pending outside a pump iteration; every iteration must flush (batched reinjection contract).");
        }
    }

    /// <summary>
    /// One (adapter handle, direction) accumulation lane: frames in append order, each flagged
    /// for whether the flush must return it to the buffer pool (pooled copies) or leave it alone
    /// (in-place capture buffers owned by the pump).
    /// </summary>
    private sealed class PendingPassLane(nint adapterHandle, bool toAdapter, int initialFrames)
    {
        public readonly nint AdapterHandle = adapterHandle;
        public readonly bool ToAdapter = toAdapter;
        public NdisPacketBuffer[] Buffers = new NdisPacketBuffer[initialFrames];
        public bool[] Rented = new bool[initialFrames];
        public int Count;
    }

    public ValueTask BlockAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        // Consumed: the lease is already completed by the dispatcher; no reinjection occurs.
        if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacket("packet.dropped", packet, new RuntimeLogField("reason", "policy"));
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken)
    {
        if (_udpProxy is { } udpProxy && packet.Context.Key.Protocol == TransportProtocol.Udp)
        {
            await HandleUdpProxyAsync(udpProxy, packet, server, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_tcpProxy is null)
        {
            LogProxyNotInitialized();
            return;
        }

        try
        {
            var outcome = await _tcpProxy.HandlePacketAsync(packet, server, cancellationToken).ConfigureAwait(false);
            if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacket("tcp.packet.handled", packet, new RuntimeLogField("outcome", outcome));
            if (outcome == TcpRedirectOutcome.Dropped)
            {
                // TIME_WAIT-grace tombstone hit: a straggler of a redirect torn down moments ago
                // (final ACK, retransmitted FIN/ACK). It is consumed silently — reinjecting toward
                // the original server would bounce an RST back at the client's finished connection,
                // and Blocked would emit a misleading proxy-unavailable warning. The lease is
                // already completed by the dispatcher. The trace follows the packet.dropped family
                // (BlockAsync logs reason=policy); packet.completed belongs to the dispatcher, which
                // emits it exactly once per packet.
                if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacket("packet.dropped", packet, new RuntimeLogField("reason", "grace"));
            }
            else if (outcome == TcpRedirectOutcome.SetupPending)
            {
                // R8: the SYN was retained by the coordinator and its listener setup continues in
                // the background; nothing was injected now and nothing failed. Consume silently
                // (same family as the grace drop — no pass, no block warning): the background
                // setup injects the rewritten SYN from the retained copy.
                if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacket("packet.dropped", packet, new RuntimeLogField("reason", "setupPending"));
            }
            else if (outcome == TcpRedirectOutcome.NotRelevant)
            {
                // The flow is proxy-decided but this packet was never the coordinator's to handle
                // (typically a connection established before capture started, so its SYN was never
                // observed). It cannot join a relay retroactively; pass it so the pre-existing
                // connection stays alive instead of hanging until timeout.
                await PassAsync(packet, cancellationToken).ConfigureAwait(false);
            }
            else if (outcome == TcpRedirectOutcome.Blocked)
            {
                LogProxyBlocked("redirect");
            }
            // Injected: the coordinator rewrote and reinjected the frame itself; the lease is
            // consumed.
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"TCP proxy handling failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Hands a UDP datagram on a proxy-decided UDP flow to the SOCKS5 UDP relay coordinator. The
    /// original datagram is consumed (the relay transport owns forwarding, including any buffered
    /// setup traffic); it is never reinjected. A parse failure or an unsent datagram fails closed
    /// without a pass downgrade.
    /// </summary>
    private async ValueTask HandleUdpProxyAsync(UdpProxyCoordinator udpProxy, CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken)
    {
        if (!IPUdpPacket.TryParse(packet.Lease.Frame, out var udpView))
        {
            if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacket("udp.packet.rejected", packet, new RuntimeLogField("reason", "parse"));
            LogProxyBlocked("parse");
            return;
        }

        // The Ethernet source MAC is the client's address (a VM NIC for forwarded flows). The
        // coordinator records it so forwarded UDP responses can be rebuilt toward the client
        // instead of this host's own NIC MAC.
        var clientMac = packet.Lease.Frame.Slice(6, 6);

        try
        {
            var sent = await udpProxy.TrySendAsync(packet.Context.Key, server, udpView.Payload, cancellationToken, packet.PacketSequence, packet.FlowGeneration, clientMac).ConfigureAwait(false);
            if (!sent)
            {
                if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogPacket("udp.packet.rejected", packet, new RuntimeLogField("reason", "send"));
                LogProxyBlocked("send");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogUdpFailureRateLimited(ex);
        }
    }

    private void LogPacket(string eventName, CapturedFlowPacket packet, params RuntimeLogField[] additional)
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Trace)) return;
        var fields = new RuntimeLogField[additional.Length + 4];
        fields[0] = new("packet", packet.PacketSequence == 0 ? null : packet.PacketSequence);
        fields[1] = new("flow", packet.FlowGeneration == 0 ? null : packet.FlowGeneration);
        fields[2] = new("protocol", packet.Context.Key.Protocol);
        fields[3] = new("stage", eventName);
        additional.CopyTo(fields, 4);
        _logger.Event(RuntimeLogLevel.Trace, eventName, fields);
    }

    /// <summary>
    /// The genuinely-uninitialized case: no proxy coordinator was wired, so no relay could ever
    /// exist. Every other blocked path reports its own reason through <see cref="LogProxyBlocked"/>
    /// instead — claiming "not initialized" for a capacity or parse rejection misleads diagnosis (S6c).
    /// </summary>
    private void LogProxyNotInitialized()
    {
        if (!ShouldWarn(ref _lastProxyUnavailableLogTicks)) return;
        _logger.Warn("A proxy-selected flow was blocked because proxy relay support is not initialized in this build.");
    }

    /// <summary>
    /// Reports why a proxy-selected flow was blocked (S6c). The reason values mirror the
    /// executor-level trace vocabulary: <c>redirect</c> (the TCP redirect coordinator rejected the
    /// flow — its per-event <c>tcp.redirect.rejected</c> trace carries the specific sub-reason),
    /// <c>parse</c>, and <c>send</c> match the <c>udp.packet.rejected</c> trace reasons.
    /// </summary>
    private void LogProxyBlocked(string reason)
    {
        if (!ShouldWarn(ref _lastProxyUnavailableLogTicks)) return;
        _logger.Warn($"A proxy-selected flow was blocked: reason={reason}.");
    }

    private void LogUdpFailureRateLimited(Exception exception)
    {
        if (!ShouldWarn(ref _lastUdpFailureLogTicks)) return;
        _logger.Warn($"UDP proxy handling failed: {exception.GetType().Name}: {exception.Message}");
    }

    /// <summary>
    /// True for the single caller allowed to warn in the current window (CAS-guarded). Checked
    /// before message formatting so a suppressed call never allocates the message string.
    /// </summary>
    private static bool ShouldWarn(ref long lastLogTicks)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref lastLogTicks);
        return now - last >= RateLimitedWarnInterval.Ticks && Interlocked.CompareExchange(ref lastLogTicks, now, last) == last;
    }
}
