using System.Runtime.ExceptionServices;
using WinForward.Configuration;
using WinForward.Core;

namespace WinForward.Runtime.UdpProxy;

// Mechanical file-size split of UdpProxyCoordinator (behavior-zero, 2026-09-18): the span-send
// bridge that lets the capture path hand a native-buffer view to the relay without materializing
// it. The coordinator's single _gate semantics, admission logic, and teardown stay in the main
// file; these members moved verbatim.
public sealed partial class UdpProxyCoordinator
{
    /// <summary>
    /// Span-based entry for the capture path (A4): the executor holds the datagram payload only as
    /// a synchronous view of the native capture buffer. The ready-session warm shape consumes the
    /// span synchronously (SOCKS5 encode into the transport's reusable buffer) so nothing
    /// materializes, while the setup-window path copies the datagram into the bounded setup queue
    /// (the queue owns its native leases).
    /// </summary>
    internal ValueTask<bool> TrySendSpanAsync(FlowKey flow, Socks5Server server, ReadOnlySpan<byte> payload, MacAddress clientMac, CancellationToken cancellationToken, long packetSequence = 0, long flowGeneration = 0)
    {
        if (flow.Protocol != TransportProtocol.Udp) throw new ArgumentException("UDP coordinator accepts only UDP flow keys.", nameof(flow));

        UdpSessionSlot? readySlot;
        UdpProxySession? readySession;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            if (_cooldowns.TryHit(flow, now))
            {
                if (_logger.IsEnabled(RuntimeLogLevel.Trace)) UdpProxyLogging.LogTrace(_logger, "udp.setup.cooldown", flow, new RuntimeLogField("reason", "cooldown"));
                return ValueTask.FromResult(false);
            }

            if (!_sessions.TryGetValue(flow, out var slot))
            {
                if (_sessions.Count >= Capacity)
                {
                    if (_logger.IsEnabled(RuntimeLogLevel.Trace)) UdpProxyLogging.LogTrace(_logger, "udp.session.rejected", flow, new RuntimeLogField("reason", "capacity"));
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
                if (!ScheduleSessionSetup(flow, server, flowGeneration, clientMac, slot))
                {
                    if (_logger.IsEnabled(RuntimeLogLevel.Trace)) UdpProxyLogging.LogTrace(_logger, "udp.session.rejected", flow, new RuntimeLogField("reason", "setupRing"));
                    return ValueTask.FromResult(false);
                }
                _sessions.Add(flow, slot);
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
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
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
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
            return false;
        }
        catch (Exception exception)
        {
            await _slotHost.RemoveSlotAsync(flow, slot, UdpTeardownReason.Fault).ConfigureAwait(false);
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
        if (_sessionUnavailableDropLog.ShouldEmit()) UdpProxyLogging.LogSessionUnavailableDrop(_logger, flow);
        return false;
    }

    private async ValueTask<bool> RemoveSlotSpanAsync(FlowKey flow, UdpSessionSlot slot, Exception exception)
    {
        await _slotHost.RemoveSlotAsync(flow, slot, UdpTeardownReason.Fault).ConfigureAwait(false);
        ExceptionDispatchInfo.Capture(exception).Throw();
        return false;
    }

    private void LogSpanDatagramSent(FlowKey flow, UdpProxySession session, long packetSequence, int bytes)
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Trace)) return;
        UdpProxyLogging.LogTrace(_logger, "udp.packet.sent", flow, new RuntimeLogField("packet", packetSequence == 0 ? null : packetSequence), new RuntimeLogField("flow", session.FlowGeneration == 0 ? null : session.FlowGeneration), new RuntimeLogField("bytes", bytes), new RuntimeLogField("udpAssociation", session.Association.Generation));
    }
}
