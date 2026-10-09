using System.Runtime.ExceptionServices;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.Logging;

namespace WinForward.Runtime.UdpProxy;

// Mechanical file-size split of UdpProxyCoordinator: the span-send bridge that lets the capture path
// hand a native-buffer view to the relay without materializing it. The coordinator's single _gate
// semantics, admission logic, and teardown stay in the main file.
public sealed partial class UdpProxyCoordinator
{
    /// <summary>
    /// Span-based entry for the capture path: the executor holds the datagram payload only as
    /// a synchronous view of the native capture buffer. The ready-session warm shape consumes the
    /// span synchronously (SOCKS5 encode into the transport's reusable buffer) so nothing
    /// materializes, while the setup-window path copies the datagram into the bounded setup queue
    /// (the queue owns its native leases). <paramref name="target"/> is read only on the admission
    /// path: a ready session already owns its transport, so the warm shape never touches it.
    /// </summary>
    internal ValueTask<bool> TrySendSpanAsync(FlowKey flow, ProxyTarget target, ReadOnlySpan<byte> payload, MacAddress clientMac, CancellationToken cancellationToken, long packetSequence = 0, long flowGeneration = 0)
    {
        if (flow.Protocol != TransportProtocol.Udp) throw new ArgumentException("UDP coordinator accepts only UDP flow keys.", nameof(flow));

        // Ready-first: a cache-resident, flow-validated session is sent without taking the coordinator
        // gate and without reading a clock. The admission path (setup in flight, a cooldown, a new flow,
        // or a cache collision/unpopulated slot) keeps both. The slot's Session is published before Ready
        // under the gate, so validating the flow key first and then reading Ready is sound.
        var cached = Volatile.Read(ref _sessionCache[SessionCacheSlot(flow)]);
        if (cached is { Session: { } session, Ready: true } && session.Flow.Equals(flow))
        {
            return SendOnReadySessionSpanAsync(flow, cached, session, payload, packetSequence, cancellationToken);
        }

        return SendAdmissionPathSpanAsync(flow, target, payload, clientMac, packetSequence, flowGeneration, cancellationToken);
    }

    private int SessionCacheSlot(FlowKey flow) => flow.GetHashCode() & (_sessionCache.Length - 1);

    private void PublishSessionSlot(FlowKey flow, UdpSessionSlot slot) => Volatile.Write(ref _sessionCache[SessionCacheSlot(flow)], slot);

    private void ClearSessionSlot(FlowKey flow, UdpSessionSlot slot)
    {
        var index = SessionCacheSlot(flow);
        if (ReferenceEquals(Volatile.Read(ref _sessionCache[index]), slot)) Volatile.Write(ref _sessionCache[index], null);
    }

    /// <summary>
    /// The admission path: everything that is not a ready cache hit. It takes the coordinator gate, reads
    /// the clock once (the cooldown probe needs it), re-checks the cooldown and the slot, admits a new
    /// slot when the flow has none, and re-checks <see cref="UdpSessionSlot.Ready"/> under the gate — a
    /// datagram first read as "not ready" must not be enqueued after the flush already drained the queue
    /// and flipped the slot ready, or it would sit until its TTL.
    /// </summary>
    private ValueTask<bool> SendAdmissionPathSpanAsync(FlowKey flow, ProxyTarget target, ReadOnlySpan<byte> payload, MacAddress clientMac, long packetSequence, long flowGeneration, CancellationToken cancellationToken)
    {
        UdpSessionSlot? readySlot;
        UdpProxySession? readySession;
        lock (_gate)
        {
            NoteGateEntry();
            ObjectDisposedException.ThrowIf(_scope.IsSealed, this);
            var now = _timeProvider.GetUtcNow();
            if (_cooldowns.TryHit(flow, now))
            {
                UdpProxyLog.UdpSetupCooldown(_logger, flow.Protocol, flow.Local, flow.Remote, "cooldown");
                return ValueTask.FromResult(false);
            }

            if (!_sessions.TryGetValue(flow, out var slot))
            {
                if (_sessions.Count >= Capacity)
                {
                    RuntimeCounters.Shared.Increment(RuntimeCounters.UdpCapacityRejections);
                    UdpProxyLog.UdpSessionRejected(_logger, flow.Protocol, flow.Local, flow.Remote, "capacity");
                    if (_capacityRejectionLog.ShouldEmit()) UdpProxyLog.UdpSessionCapacityBlock(_logger, flow.Protocol, flow.Local, flow.Remote, "capacity", Capacity);
                    return ValueTask.FromResult(false);
                }

                slot = new UdpSessionSlot();
                // The client MAC is an inline value: it is copied by value into the setup task,
                // which runs off the pump AND off the coordinator gate, so even a synchronously
                // completing factory never blocks other flows' dispatch. Registration ordering
                // (the slot must be in _sessions before the session's receive-failure handler can
                // run) is carried by this gate: the setup pipeline's attach step (which takes this
                // gate via its delegate) can only be acquired after this critical section
                // (including the Add below) has released it.
                if (!ScheduleSessionSetup(flow, target, flowGeneration, clientMac, slot))
                {
                    RuntimeCounters.Shared.Increment(RuntimeCounters.UdpSetupRejections);
                    UdpProxyLog.UdpSessionRejected(_logger, flow.Protocol, flow.Local, flow.Remote, "setupRing");
                    return ValueTask.FromResult(false);
                }
                _sessions.Add(flow, slot);
                PublishSessionSlot(flow, slot);
            }

            if (slot.Ready)
            {
                readySlot = slot;
                readySession = slot.Session!;
            }
            else
            {
                return ValueTask.FromResult(EnqueueSetupDatagram(flow, slot, payload));
            }
        }

        var result = SendOnReadySessionSpanAsync(flow, readySlot, readySession, payload, packetSequence, cancellationToken);
        return result;
    }

    /// <summary>
    /// The ready-session send bridge: a non-async method (the payload span must not cross an
    /// await) that starts the send and either completes it inline — the warm shape the transport
    /// finishes synchronously — or hands only the send tail to the async continuation. Caller
    /// cancellation propagates untouched; a genuine transport failure removes the slot first and
    /// then rethrows the original exception, while a session that refuses the datagram because it
    /// is expiring or faulted is a counted drop that leaves the slot to its state owner.
    /// </summary>
    private ValueTask<bool> SendOnReadySessionSpanAsync(FlowKey flow, UdpSessionSlot slot, UdpProxySession session, ReadOnlySpan<byte> payload, long packetSequence, CancellationToken cancellationToken)
    {
        ValueTask<bool> send;
        try
        {
            send = session.SendSpanAsync(flow.Remote, payload, cancellationToken);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && !_scope.IsSealed)
        {
            // Cancellation from this individual caller is not evidence that the
            // shared UDP transport failed.
            return ValueTask.FromException<bool>(exception);
        }
        catch (Exception exception)
        {
            return RemoveSlotSpanAsync(flow, slot, exception);
        }

        if (send.IsCompletedSuccessfully)
        {
            // Steady-state path: the session completed inline, so consuming its result here is a
            // plain allocation-free read (the same guarded-inline shape as NdisCapture's handler
            // result, which suppresses the sibling VSTHRD002); the await path is the tail below.
#pragma warning disable VSTHRD103, MA0042 // The ValueTask is already complete (checked above), so reading it cannot block; MA0042's await guidance does not apply to the guarded-inline fast path.
            var sent = send.GetAwaiter().GetResult();
#pragma warning restore VSTHRD103, MA0042
            if (!sent) return ValueTask.FromResult(DropSessionUnavailable(flow));
            LogSpanDatagramSent(flow, session, packetSequence, payload.Length);
            return ValueTask.FromResult(true);
        }
        return SendSpanTailAsync(send, flow, slot, session, packetSequence, payload.Length, cancellationToken);
    }

    private async ValueTask<bool> SendSpanTailAsync(ValueTask<bool> send, FlowKey flow, UdpSessionSlot slot, UdpProxySession session, long packetSequence, int payloadLength, CancellationToken cancellationToken)
    {
        bool sent;
        try
        {
            sent = await send.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && !_scope.IsSealed)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
            return false;
        }
        catch (Exception exception)
        {
            await _slotHost.RemoveSlotAsync(flow, slot, TeardownReasonFor(exception)).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(exception).Throw();
            return false;
        }

        if (!sent) return DropSessionUnavailable(flow);
        LogSpanDatagramSent(flow, session, packetSequence, payloadLength);
        return true;
    }

    /// <summary>
    /// Records a datagram the session refused because it is expiring or faulted. The slot is
    /// deliberately left in place — the sweeper owns an expiring session's removal and the
    /// failure handler a faulted one's — so this is a counted, rate-limited drop rather than a
    /// transport failure.
    /// </summary>
    private bool DropSessionUnavailable(FlowKey flow)
    {
        RuntimeCounters.Shared.Increment(RuntimeCounters.UdpFailClosedDrop);
        if (_sessionUnavailableDropLog.ShouldEmit()) UdpProxyLog.UdpSendDropped(_logger, flow.Protocol, flow.Local, flow.Remote, "sessionUnavailable");
        return false;
    }

    private async ValueTask<bool> RemoveSlotSpanAsync(FlowKey flow, UdpSessionSlot slot, Exception exception)
    {
        await _slotHost.RemoveSlotAsync(flow, slot, TeardownReasonFor(exception)).ConfigureAwait(false);
        ExceptionDispatchInfo.Capture(exception).Throw();
        return false;
    }

    /// <summary>
    /// The teardown reason a fault carries, the single decision point shared by every removal path:
    /// a relay handshake rejected after the transport's setup call returned is
    /// <see cref="UdpTeardownReason.SetupFailure"/> — the reason that arms the setup cooldown (and
    /// that the setup pipeline counts as a genuine setup failure), exactly as a refused
    /// <c>UDP ASSOCIATE</c> is on the native path, so a systematically rejecting server is re-dialed
    /// once per cooldown; an association that died without recovering is
    /// <see cref="UdpTeardownReason.AssociationLost"/> (counted, and deliberately without the setup
    /// cooldown — recovery is the flow's next datagram); everything else stays a generic
    /// <see cref="UdpTeardownReason.Fault"/>. Only the association-lost branch counts here, so a
    /// rejection is never counted as an association loss.
    /// <para>
    /// Shared with the setup pipeline, whose flush window is the same send path: an association lost
    /// while the setup queue drains is not a setup failure.
    /// </para>
    /// </summary>
    internal static UdpTeardownReason TeardownReasonFor(Exception exception)
    {
        if (exception is UdpTransportHandshakeRejectedException) return UdpTeardownReason.SetupFailure;
        if (exception is not UdpAssociationLostException) return UdpTeardownReason.Fault;
        RuntimeCounters.Shared.Increment(RuntimeCounters.UdpAssociationLost);
        return UdpTeardownReason.AssociationLost;
    }

    private void LogSpanDatagramSent(FlowKey flow, UdpProxySession session, long packetSequence, int bytes)
    {
        UdpProxyLog.UdpPacketSent(
            _logger,
            flow.Protocol,
            flow.Local,
            flow.Remote,
            packetSequence == 0 ? null : packetSequence,
            session.FlowGeneration == 0 ? null : session.FlowGeneration,
            bytes,
            session.Association.Generation);
    }
}
