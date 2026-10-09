using WinForward.Core;
using WinForward.Runtime.Logging;

namespace WinForward.Runtime.UdpProxy;

// The idle-expiry sweep lives in its own file rather than UdpProxyCoordinator's main file, which
// is at the 400-effective-line cap; UdpProxyCoordinator.Send.cs set the precedent of splitting on
// a seam instead of growing it.
public sealed partial class UdpProxyCoordinator
{
    /// <summary>
    /// Removes sessions whose last send or receive is older than <paramref name="idleTimeout"/>
    /// and releases their associations, so short-lived DNS/QUIC-style flows do not accumulate to
    /// the bounded capacity. Also prunes expired setup cooldowns. Idle expiry runs on a periodic
    /// sweep in the runtime.
    /// <para>
    /// The scan collects candidates into a reused scratch under one gate hold, then tears them down
    /// outside the gate, re-verifying each candidate with
    /// <see cref="UdpProxySession.TryBeginExpiry(DateTimeOffset, TimeSpan, TimeSpan)"/>.
    /// The whole tick is single-flight through <see cref="_sweepGate"/> because the scratch's lifetime is not
    /// covered by <c>_gate</c> and this method is public.
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
                var flow = session.Flow;
                UdpProxyLog.UdpSessionExpired(_logger, session.FlowGeneration == 0 ? null : session.FlowGeneration, session.Association.Generation, flow.Protocol, flow.Local, flow.Remote);
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
