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

        UdpSessionSlot? readySlot = null;
        UdpProxySession? readySession = null;
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
                if (_sessions.Count >= _capacity)
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

        var result = SendOnReadySessionSpanAsync(flow, readySlot!, readySession!, payload, cancellationToken, packetSequence);
        return result;
    }

    /// <summary>
    /// The ready-session send bridge: a non-async method (the payload span must not cross an
    /// await) that starts the send and either completes it inline — the warm shape the transport
    /// finishes synchronously — or hands only the send tail to the async continuation. Caller
    /// cancellation propagates untouched; any other send failure removes the slot first and then
    /// rethrows the original exception.
    /// </summary>
    private ValueTask<bool> SendOnReadySessionSpanAsync(FlowKey flow, UdpSessionSlot slot, UdpProxySession session, ReadOnlySpan<byte> payload, CancellationToken cancellationToken, long packetSequence)
    {
        ValueTask send;
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
            LogSpanDatagramSent(flow, session, packetSequence, payload.Length);
            return ValueTask.FromResult(true);
        }
        return SendSpanTailAsync(send, flow, slot, session, packetSequence, payload.Length, cancellationToken);
    }

    private async ValueTask<bool> SendSpanTailAsync(ValueTask send, FlowKey flow, UdpSessionSlot slot, UdpProxySession session, long packetSequence, int payloadLength, CancellationToken cancellationToken)
    {
        try
        {
            await send.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
            return false;
        }
        catch (Exception exception)
        {
            await _slotHost.RemoveSlotAsync(flow, slot, armCooldown: false).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(exception).Throw();
            return false;
        }

        LogSpanDatagramSent(flow, session, packetSequence, payloadLength);
        return true;
    }

    private async ValueTask<bool> RemoveSlotSpanAsync(FlowKey flow, UdpSessionSlot slot, Exception exception)
    {
        await _slotHost.RemoveSlotAsync(flow, slot, armCooldown: false).ConfigureAwait(false);
        ExceptionDispatchInfo.Capture(exception).Throw();
        return false;
    }

    private void LogSpanDatagramSent(FlowKey flow, UdpProxySession session, long packetSequence, int bytes)
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Trace)) return;
        UdpProxyLogging.LogTrace(_logger, "udp.packet.sent", flow, new RuntimeLogField("packet", packetSequence == 0 ? null : packetSequence), new RuntimeLogField("flow", session.FlowGeneration == 0 ? null : session.FlowGeneration), new RuntimeLogField("bytes", bytes), new RuntimeLogField("udpAssociation", session.Association.Generation));
    }
}
