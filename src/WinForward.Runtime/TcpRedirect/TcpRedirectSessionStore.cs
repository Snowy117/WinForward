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
/// dictionary, the disposed flag, the dispose task, and the inflight-setup drain counter happens
/// under one gate, so registration, teardown, expiry, and dispose remain mutually exclusive. It is
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
internal sealed class TcpRedirectSessionStore
{
    private readonly TcpRedirectTable _table;
    private readonly TcpRedirectTombstoneTable _tombstones;
    private readonly IRuntimeLogger _logger;
    private readonly Dictionary<FlowKey, TcpRedirectSession> _sessions = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private TaskCompletionSource _setupsDrained = CompletedSource();
    private Task? _disposeTask;
    private int _inflightSetups;
    private bool _disposed;

    /// <summary>
    /// The TIME_WAIT-grace window a torn-down redirect stays resolvable as a tombstone. 60s covers
    /// the handshake tail (the client's final ACK) and common FIN retransmissions (RTO backoff
    /// typically stays under 10s) without parking entries for a full 240s TIME_WAIT.
    /// </summary>
    private static readonly TimeSpan TombstoneGracePeriod = TimeSpan.FromSeconds(60);

    public TcpRedirectSessionStore(TcpRedirectTable table, IRuntimeLogger logger, int capacity)
    {
        _table = table;
        _logger = logger;
        _tombstones = new TcpRedirectTombstoneTable(capacity);
    }

    public bool IsDisposed => Volatile.Read(ref _disposed);

    public CancellationToken ShutdownToken => _shutdown.Token;

    /// <summary>The TIME_WAIT-grace tombstone index; surfaced for the coordinator's routing lookups.</summary>
    public TcpRedirectTombstoneTable Tombstones => _tombstones;

    /// <summary>The live session count, read under the gate (used by the coordinator's capacity check).</summary>
    public int SessionCount
    {
        get { lock (_gate) return _sessions.Count; }
    }

    public void EnterSetup()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_inflightSetups++ == 0) _setupsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void ExitSetup()
    {
        lock (_gate)
        {
            if (--_inflightSetups == 0) _setupsDrained.TrySetResult();
        }
    }

    /// <summary>
    /// Adds a newly built session under the gate. When the store is already disposed the session
    /// never becomes observable: it is retired, its lifetime disposed, and its self-traffic token
    /// released before returning null.
    /// </summary>
    public TcpRedirectSession? TryRegister(TcpRedirectSession session)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                session.Retire();
                session.DisposeLifetime();
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
    /// deliberately not expired here (M4); its teardown is tied to relay completion.
    /// </summary>
    public ValueTask<int> RemoveExpiredAsync(DateTimeOffset now, TimeSpan idleTimeout) => RemoveExpiredAsync(now, idleTimeout, prunePending: null);

    /// <summary>
    /// Same sweep with an optional pending-SYN prune hook (R8): the coordinator's retained-SYN
    /// TTL and setup-cooldown expiry ride this existing sweep tick (no dedicated timer), running
    /// inside the same sweep phase as the store's own expiry so their clocks agree.
    /// </summary>
    public async ValueTask<int> RemoveExpiredAsync(DateTimeOffset now, TimeSpan idleTimeout, Action? prunePending)
    {
        prunePending?.Invoke();
        RetiredSession[] expired;
        lock (_gate)
        {
            expired = _sessions.Values
                .Where(session => session.Association.Phase == RelayPhase.Redirecting && now - session.Association.LastActivityUtc >= idleTimeout)
                .Select(RetireSessionUnderGate)
                .ToArray();
        }

        foreach (var retired in expired)
        {
            await ReleaseRetiredAsync(retired).ConfigureAwait(false);
        }

        // Rides the same sweep tick (no dedicated timer): tombstones whose grace window elapsed are
        // reclaimed here, after which same-tuple packets fall back to the pre-tombstone behavior.
        // Tombstones are not included in the return value — it counts expired redirect sessions,
        // keeping the sweeper's runtime.expired accounting unchanged.
        _tombstones.RemoveExpired(now);
        return expired.Length;
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        TaskCompletionSource? start = null;
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                disposeTask = _disposeTask;
            }
            else
            {
                _disposed = true;
                start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                disposeTask = _disposeTask = start.Task;
            }
        }

        if (start is not null) _ = RunDisposeAsync(start);
        return new ValueTask(disposeTask);
    }

    private async Task RunDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        Task setupsDrained;
        lock (_gate) setupsDrained = _setupsDrained.Task;
        await setupsDrained.ConfigureAwait(false);

        RetiredSession[] sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.Select(RetireSessionUnderGate).ToArray();
        }

        foreach (var retired in sessions)
        {
            await ReleaseRetiredAsync(retired).ConfigureAwait(false);
            var session = retired.Session;
            if (session.AcceptLoop is not null)
            {
                try { await session.AcceptLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { /* cancellation is the expected shutdown path */ }
                catch (ObjectDisposedException) { /* the listener was already disposed during shutdown */ }
            }
        }

        _shutdown.Dispose();
    }

    public ValueTask TearDownSessionAsync(TcpRedirectSession session)
    {
        RetiredSession? retired;
        lock (_gate)
        {
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
        TcpRedirectLogging.LogDebug(_logger, "tcp.redirect.closed", session, "closed");
        try
        {
            // The retire critical section already removed the table alias and armed the
            // tombstone; only the disposals trail here.
            await DisposeListenerAndTokenAsync(session.Listener, session.SelfTrafficToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Warn($"TCP redirect listener disposal failed ({exception.GetType().Name}).");
        }
        if (retired.Relay is not null)
        {
            try { await retired.Relay.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { _logger.Warn($"TCP redirect relay disposal failed ({exception.GetType().Name})."); }
        }
        session.DisposeLifetime();
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
        lock (_gate) _sessions.TryGetValue(association.OriginalKey, out session);
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
            try { await listener.DisposeAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* the listener may already be disposed during teardown */ }
        }
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
        _table.TryRemove(association, removed => _tombstones.TryAdd(removed.OriginalKey, removed.ReverseSourceEndpoint, removed.ReverseDestinationEndpoint, DateTimeOffset.UtcNow + TombstoneGracePeriod));
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
