using WinForward.Core;

namespace WinForward.Runtime.UdpProxy;

// Mechanical file-size split of UdpProxyCoordinator (behavior-zero): the idle-expiry sweep moved
// here verbatim — plus its new two-class overload — because the coordinator's main file is at the
// 400-effective-line cap and UdpProxyCoordinator.Send.cs is the established precedent for splitting
// on a seam rather than growing it.
public sealed partial class UdpProxyCoordinator
{
    /// <summary>
    /// Removes sessions whose last send or receive is older than <paramref name="idleTimeout"/>
    /// and releases their associations, so short-lived DNS/QUIC-style flows do not accumulate to
    /// the bounded capacity. Also prunes expired setup cooldowns. Idle expiry
    /// (design §7/§8) runs on a periodic sweep in the runtime.
    /// <para>
    /// The scan collects candidates into a reused scratch under one gate hold, then the teardown loop runs
    /// outside the gate with the same <see cref="UdpProxySession.TryBeginExpiry(DateTimeOffset, TimeSpan, TimeSpan)"/> re-verifier as before.
    /// The whole tick is single-flight through <see cref="_sweepGate"/> because the scratch's lifetime is no
    /// longer covered by <c>_gate</c> and this method is public.
    /// </para>
    /// </summary>
    public ValueTask<int> RemoveExpiredAsync(DateTimeOffset now, TimeSpan idleTimeout) =>
        RemoveExpiredAsync(now, idleTimeout, idleTimeout);

    /// <summary>
    /// The two-class sweep: a session whose exchange is over (<paramref name="oneShotIdleTimeout"/>,
    /// see <see cref="UdpProxySession.TryBeginExpiry(DateTimeOffset, TimeSpan, TimeSpan)"/>) retires on
    /// the short class, everything else on <paramref name="idleTimeout"/>. The two-argument overload
    /// passes one timeout for both and therefore keeps uniform retention exactly.
    /// <para>
    /// The pre-filter compares the <em>later</em> cutoff (the shorter of the two timeouts) so the
    /// candidate set remains a superset of both classes — it is a pre-filter only, and the
    /// authoritative per-class cutoff is recomputed inside <c>TryBeginExpiry</c> under the session's
    /// own gate, which is what keeps retirement class-relative and never early. Passing
    /// <paramref name="oneShotIdleTimeout"/> equal to or greater than <paramref name="idleTimeout"/>
    /// collapses the classes.
    /// </para>
    /// </summary>
    public async ValueTask<int> RemoveExpiredAsync(DateTimeOffset now, TimeSpan idleTimeout, TimeSpan oneShotIdleTimeout)
    {
#pragma warning disable MA0040 // QuiescenceScope.Token throws once the scope is disposed; a late tick must still return cleanly, and the gate is a single-flight guard rather than a cancellation point.
        await _sweepGate.WaitAsync().ConfigureAwait(false);
#pragma warning restore MA0040
        try
        {
            // Cleared at the start of the critical section: the candidates stay live in _sessions, so an
            // aborted tick is simply re-discovered by the next scan.
            _idleScratch.Clear();
            var cutoffBucket = ActivityBucket.Cutoff(now, oneShotIdleTimeout < idleTimeout ? oneShotIdleTimeout : idleTimeout);
            lock (_gate)
            {
                NoteGateEntry();
                _cooldowns.PruneExpired(now);
                foreach (var slot in _sessions.Values)
                {
                    // A pre-filter only: TryBeginExpiry re-checks the candidate's own class cutoff under the
                    // session's gate, so a candidate collected one bucket early can never retire early.
                    if (slot.Session is { } session && session.ActivityBucketForDiagnostics < cutoffBucket)
                    {
                        _idleScratch.Add((slot, session));
                    }
                }
            }

            if (_idleScratch.Count > 0 && _beforeExpiryRecheck is not null) await _beforeExpiryRecheck().ConfigureAwait(false);

            var removed = 0;
            for (var index = 0; index < _idleScratch.Count; index++)
            {
                var (slot, session) = _idleScratch[index];
                if (!session.TryBeginExpiry(now, idleTimeout, oneShotIdleTimeout)) continue;
                if (!await _slotHost.RemoveSlotAsync(session.Flow, slot, UdpTeardownReason.Expiry).ConfigureAwait(false))
                {
                    session.CancelExpiry();
                    continue;
                }
                UdpProxyLogging.LogDebug(_logger, "udp.session.expired", session.Flow, session.FlowGeneration, session.Association, serverName: null);
                removed++;
            }

            return removed;
        }
        finally
        {
            _sweepGate.Release();
        }
    }
}
