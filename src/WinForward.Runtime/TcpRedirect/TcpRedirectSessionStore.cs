using WinForward.Core;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// A retired session and its (already detached) relay, captured atomically under the store gate.
/// Retire itself already removed the table alias and armed the tombstone inside that critical
/// section (R2); only the trailing disposals (listener, relay, self-traffic token, lifetime CTS)
/// run outside the lock.
/// </summary>
internal sealed record RetiredSession(TcpRedirectSession Session, ITcpRelay? Relay);

/// <summary>
/// Owns the TCP redirect session set and its lifecycle invariants: every mutation of the session
/// dictionary happens under one gate, so registration, teardown, expiry, and dispose remain mutually
/// exclusive. The store's lifetime CTS and its inflight-setup drain live in a
/// <see cref="QuiescenceScope"/> (D7): <c>DisposeAsync</c> seals and drains it, which is what joins
/// every registered setup before the ordered session teardown runs. It is
/// also the single tombstone write point: every teardown path funnels through
/// <see cref="RemoveAssociationFromTable"/>, which records the TIME_WAIT-grace tombstone when the
/// removal wins. The retire path removes the table alias and arms the tombstone while still
/// holding the store gate, so session-dictionary removal, Phase=Closing, lifetime retire, table
/// alias removal, and tombstone arming are ONE atomic step — a same-tuple packet can never land
/// in a window where the session is gone but the alias still resolves (R2); only disposal trails
/// outside the gate. The resulting lock order is store gate → table gate → tombstone gate; it is
/// acyclic repo-wide (no path acquires them in reverse), and the table/tombstone critical
/// sections are synchronous and non-blocking, so nesting them under the store gate is safe.
/// The coordinator and setup pipeline reach the session set only through this module's methods.
/// </summary>
internal sealed class TcpRedirectSessionStore(TcpRedirectTable table, IRuntimeLogger logger, int capacity, TimeProvider timeProvider)
{
    private readonly Dictionary<FlowKey, TcpRedirectSession> _sessions = [];
    private readonly Lock _gate = new();
    private readonly QuiescenceScope _scope = new();

    /// <summary>
    /// The TIME_WAIT-grace window a torn-down redirect stays resolvable as a tombstone. 60s covers
    /// the handshake tail (the client's final ACK) and common FIN retransmissions (RTO backoff
    /// typically stays under 10s) without parking entries for a full 240s TIME_WAIT.
    /// </summary>
    private static readonly TimeSpan s_tombstoneGracePeriod = TimeSpan.FromSeconds(60);

    public bool IsDisposed => _scope.IsSealed;

    public CancellationToken ShutdownToken => _scope.Token;

    /// <summary>The TIME_WAIT-grace tombstone index; surfaced for the coordinator's routing lookups.</summary>
    public TcpRedirectTombstoneTable Tombstones { get; } = new(capacity);

    /// <summary>The live session count, read under the gate (used by the coordinator's capacity check).</summary>
    public int SessionCount
    {
        get { lock (_gate) return _sessions.Count; }
    }

    /// <summary>
    /// Admits one in-flight setup into the store's quiescence scope. Returns false once disposal has
    /// sealed the scope, in which case the caller must not start the setup and must unwind without a
    /// cooldown. The lease is disposed by the caller when the setup completes.
    /// </summary>
    public bool TryEnterSetup(out WorkLease lease) => _scope.TryEnter(out lease);

    /// <summary>
    /// Adds a newly built session under the gate. When the store is already disposed the session
    /// never becomes observable: it is retired and its self-traffic token released before returning
    /// null. The caller owns the session's lifetime drain (it has the only await that release needs).
    /// </summary>
    public TcpRedirectSession? TryRegister(TcpRedirectSession session)
    {
        lock (_gate)
        {
            if (_scope.IsSealed)
            {
                session.Retire();
                session.SelfTrafficToken.Dispose();
                return null;
            }

            _sessions.Add(session.Association.OriginalKey, session);
            return session;
        }
    }

    /// <summary>Whether an active session exists for the flow (the gate-held half of the hold predicate).</summary>
    public bool Holds(FlowKey key)
    {
        lock (_gate)
        {
            if (_sessions.ContainsKey(key)) return true;
        }
        return false;
    }

    /// <summary>
    /// Retires and releases every half-open session still <see cref="RelayPhase.Redirecting"/> with
    /// no activity for the idle timeout, then reclaims elapsed tombstones. A relaying session is
    /// deliberately not expired here (M4); its teardown is tied to relay completion. The optional
    /// pending-SYN prune hook (R8) rides this existing sweep tick (no dedicated timer), running
    /// inside the same sweep phase as the store's own expiry so their clocks agree.
    /// </summary>
    public async ValueTask<int> RemoveExpiredAsync(DateTimeOffset now, TimeSpan idleTimeout, Action? prunePending)
    {
        prunePending?.Invoke();
        RetiredSession[] expired;
        lock (_gate)
        {
            if (_scope.IsSealed) return 0;
            expired = [.. _sessions.Values
                .Where(session => session.Association.Phase == RelayPhase.Redirecting && now - session.Association.LastActivityUtc >= idleTimeout)
                .Select(RetireSessionUnderGate)];
        }

        foreach (var retired in expired)
        {
            await ReleaseRetiredAsync(retired).ConfigureAwait(false);
        }

        // Rides the same sweep tick (no dedicated timer): tombstones whose grace window elapsed are
        // reclaimed here, after which same-tuple packets fall back to the pre-tombstone behavior.
        // Tombstones are not included in the return value — it counts expired redirect sessions,
        // keeping the sweeper's runtime.expired accounting unchanged.
        Tombstones.RemoveExpired(now);
        return expired.Length;
    }

    public ValueTask DisposeAsync() => new(DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        // The scope owns the store's lifetime CTS (D7) and joins every in-flight setup, so the
        // ordered teardown keeps its historical shape with the setup drain as the scope's join:
        // cancel the token, drain the setups, retire the sessions, then await their accept loops.
        _scope.Cancel();
        await _scope.DrainAsync().ConfigureAwait(false);

        RetiredSession[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions.Values.Select(RetireSessionUnderGate)];
        }

        foreach (var retired in sessions)
        {
            await ReleaseRetiredAsync(retired).ConfigureAwait(false);
            var session = retired.Session;
            if (session.AcceptLoop is not null)
            {
                // The loop's own finally drains the session, so its lifetime source is already
                // released by the time this await observes the end — cancellation is the expected
                // shutdown path.
                try { await session.AcceptLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* the expected shutdown path */ }
                catch (ObjectDisposedException) { /* the listener was already disposed during shutdown */ }
            }
            // The accept loop owns the lifetime CTS disposal (R1); a session whose loop was never
            // launched has no other owner, so it is released here after the quiescence wait.
            await session.DisposeLifetimeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask TearDownSessionAsync(TcpRedirectSession session)
    {
        RetiredSession? retired;
        lock (_gate)
        {
            if (_scope.IsSealed) return ValueTask.CompletedTask;
            retired = TryRetireSessionUnderGate(session);
        }
        return retired is not null ? ReleaseRetiredAsync(retired) : ValueTask.CompletedTask;
    }

    private RetiredSession? TryRetireSessionUnderGate(TcpRedirectSession session)
    {
        if (!_sessions.TryGetValue(session.Association.OriginalKey, out var current) || !ReferenceEquals(current, session)) return null;
        return RetireSessionUnderGate(session);
    }

    private RetiredSession RetireSessionUnderGate(TcpRedirectSession session)
    {
        _sessions.Remove(session.Association.OriginalKey);
        session.Association.Phase = RelayPhase.Closing;
        session.Retire();
        var relay = session.Relay;
        session.Relay = null;
        // Runs while the store gate is still held (R2): a same-tuple SYN or data packet can
        // never observe the session gone from the dictionary while the alias still resolves —
        // the removal and the grace tombstone land in the same critical section as the retire.
        // Lock order store → table → tombstone is documented on the class.
        RemoveAssociationFromTable(session.Association);
        return new RetiredSession(session, relay);
    }

    private async ValueTask ReleaseRetiredAsync(RetiredSession retired)
    {
        var session = retired.Session;
        TcpRedirectLogging.LogDebug(logger, "tcp.redirect.closed", session, "closed");
        try
        {
            // The retire critical section already removed the table alias and armed the
            // tombstone; only the disposals trail here.
            await DisposeListenerAndTokenAsync(session.Listener, session.SelfTrafficToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.Warn($"TCP redirect listener disposal failed ({exception.GetType().Name}).");
        }
        if (retired.Relay is not null)
        {
            try { await retired.Relay.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { logger.Warn($"TCP redirect relay disposal failed ({exception.GetType().Name})."); }
        }
        // The lifetime CTS is disposed by the accept loop once it ends (it is the only reader of
        // session.Token), so a retire can never pull the CTS out from under a concurrent Token
        // read (R1). A session whose loop was never launched is disposed here.
        if (session.AcceptLoop is null) await session.DisposeLifetimeAsync().ConfigureAwait(false);
    }

    public bool TryAttachRelay(TcpRedirectSession session, ITcpRelay relay)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session.Association.OriginalKey, out var current) || !ReferenceEquals(current, session) || session.IsRetired || session.Association.Phase != RelayPhase.Redirecting || session.Relay is not null) return false;
            session.Relay = relay;
            session.Association.Phase = RelayPhase.Relaying;
            return true;
        }
    }

    public async ValueTask FailAssociationAsync(TcpRedirectAssociation association)
    {
        TcpRedirectSession? session;
        lock (_gate)
        {
            if (_scope.IsSealed) return;
            _sessions.TryGetValue(association.OriginalKey, out session);
        }
        if (session is not null) await TearDownSessionAsync(session).ConfigureAwait(false);
        else RemoveAssociationFromTable(association);
    }

    /// <summary>
    /// Releases an association that never had a registered session (the store was disposed during
    /// setup registration): removes the table alias with its grace tombstone, then disposes the
    /// listener and the self-traffic token. The retire path does NOT come through here — its alias
    /// removal and tombstone already ran atomically inside <see cref="RetireSessionUnderGate"/>.
    /// </summary>
    public async ValueTask ReleaseAssociationAsync(ITcpRedirectListener listener, TcpRedirectAssociation association, SelfTrafficRegistry.SelfTrafficToken? selfTrafficToken)
    {
        RemoveAssociationFromTable(association);
        await DisposeListenerAndTokenAsync(listener, selfTrafficToken).ConfigureAwait(false);
    }

    private static async ValueTask DisposeListenerAndTokenAsync(ITcpRedirectListener listener, SelfTrafficRegistry.SelfTrafficToken? selfTrafficToken)
    {
        try
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { /* the listener may already be disposed during teardown */ }
        finally
        {
            selfTrafficToken?.Dispose();
        }
    }

    /// <summary>
    /// Removes the association from the redirect table and, when the removal wins, records a
    /// TIME_WAIT-grace tombstone under both of its lookup keys. The tombstone write runs inside
    /// the table's removal critical section, so once the flow is observably gone its tombstone is
    /// already armed — a straggler can never race between the two. Every teardown path — relay
    /// completion, relay setup failure, fail-closed release, and global dispose — funnels through
    /// here, so this is the single tombstone write point covering all entries. Within the grace
    /// window, stragglers of the finished handshake resolve as <see cref="TcpRedirectOutcome.Dropped"/>
    /// instead of NotRelevant, whose executor fallback would reinject toward the real server —
    /// a server that never saw the proxied connection and answers the unknown tuple with a
    /// bounced RST. Tombstones occupy neither the redirect table's claim capacity nor the session
    /// budget.
    /// </summary>
    private void RemoveAssociationFromTable(TcpRedirectAssociation association)
    {
        table.TryRemove(association, removed => Tombstones.TryAdd(removed.OriginalKey, removed.ReverseSourceEndpoint, removed.ReverseDestinationEndpoint, timeProvider.GetUtcNow() + s_tombstoneGracePeriod));
    }
}
