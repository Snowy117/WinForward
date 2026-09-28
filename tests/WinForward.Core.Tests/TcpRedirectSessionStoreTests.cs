using System.Net;
using WinForward.Runtime.TcpRedirect;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// R2: the store is the single teardown authority. DisposeAsync is single-flight and, once it has
/// begun, a late teardown or fail-closed release must not re-enter — no second relay release, no
/// second session, no second tombstone.
/// </summary>
public sealed class TcpRedirectSessionStoreTests
{
    [Fact]
    public async Task DisposeIsSingleFlightAndLateTeardownNeverReEnters()
    {
        var table = new TcpRedirectTable(capacity: 8);
        var store = new TcpRedirectSessionStore(table, new RecordingRuntimeLogger(), capacity: 8, TimeProvider.System);
        var association = ClaimAssociation(table);
        var listener = new FakeListener(association.TranslatedListenerTuple);
        var session = TcpCoordinatorFakes.CreateSession(association, listener);
        Assert.Same(session, store.TryRegister(session));
        var relay = new CountingRelay();
        Assert.True(store.TryAttachRelay(session, relay));

        var first = store.DisposeAsync();
        var second = store.DisposeAsync();
        await first;
        await second;

        Assert.True(store.IsDisposed);
        Assert.True(listener.IsDisposed);
        Assert.Equal(0, store.SessionCount);
        Assert.Equal(1, relay.DisposeCount);
        Assert.Equal(0, table.Count);
        Assert.True(store.Tombstones.TryHit(association.OriginalKey, DateTimeOffset.UtcNow));

        await store.TearDownSessionAsync(session);
        await store.FailAssociationAsync(association);
        await store.RemoveExpiredAsync(DateTimeOffset.UtcNow, TimeSpan.Zero, prunePending: null);

        Assert.Equal(0, store.SessionCount);
        Assert.Equal(1, relay.DisposeCount);
    }

    [Fact]
    public async Task RegistrationLosingTheDisposeRaceLeavesTheAssociationReleasable()
    {
        // The setup path can lose the registration race: the store was disposed while the session
        // was being prepared (its table alias is already claimed). TryRegister must retire it, and
        // the caller's ReleaseAssociationAsync must still consume the alias and the token.
        var table = new TcpRedirectTable(capacity: 8);
        var store = new TcpRedirectSessionStore(table, new RecordingRuntimeLogger(), capacity: 8, TimeProvider.System);
        var association = ClaimAssociation(table);
        var listener = new FakeListener(association.TranslatedListenerTuple);
        var session = TcpCoordinatorFakes.CreateSession(association, listener);

        await store.DisposeAsync();
        Assert.Null(store.TryRegister(session));

        await store.ReleaseAssociationAsync(listener, association, session.SelfTrafficToken);

        Assert.True(session.IsRetired);
        Assert.Equal(0, table.Count);
        Assert.True(store.Tombstones.TryHit(association.OriginalKey, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task DisposeWaitsForTheRegisteredSetupLease()
    {
        var table = new TcpRedirectTable(capacity: 8);
        var store = new TcpRedirectSessionStore(table, new RecordingRuntimeLogger(), capacity: 8, TimeProvider.System);
        Assert.True(store.TryEnterSetup(out var lease));

        var dispose = store.DisposeAsync();
        Assert.True(store.IsDisposed);
        Assert.False(dispose.IsCompleted);

        lease.Dispose();
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TryEnterSetupIsRefusedOnceDisposed()
    {
        var table = new TcpRedirectTable(capacity: 8);
        var store = new TcpRedirectSessionStore(table, new RecordingRuntimeLogger(), capacity: 8, TimeProvider.System);

        await store.DisposeAsync();

        Assert.True(store.IsDisposed);
        Assert.False(store.TryEnterSetup(out _));
    }

    [Fact]
    public async Task RetireIsOrderedBeforeTheRelayIsDisposed()
    {
        // The acceptor awaits a session's relay completion against the session token; the lifetime
        // must already be cancelled (Retire) when the relay is disposed, so that wait unwinds as a
        // cancellation rather than as a stall verdict — and a stall verdict injects a client reset.
        var table = new TcpRedirectTable(capacity: 8);
        var store = new TcpRedirectSessionStore(table, new RecordingRuntimeLogger(), capacity: 8, TimeProvider.System);
        var association = ClaimAssociation(table);
        var listener = new FakeListener(association.TranslatedListenerTuple);
        var session = TcpCoordinatorFakes.CreateSession(association, listener);
        Assert.Same(session, store.TryRegister(session));
        var relay = new CountingRelay(() => session.IsRetired);
        Assert.True(store.TryAttachRelay(session, relay));

        await store.DisposeAsync();

        Assert.Equal(1, relay.DisposeCount);
        Assert.True(relay.RetiredAtDispose);
    }

    private static TcpRedirectAssociation ClaimAssociation(TcpRedirectTable table)
    {
        var local = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000);
        var remote = Endpoint.From(IPAddress.Parse("192.0.2.53"), 443);
        var key = FlowKey.Create(local, remote, TransportProtocol.Tcp, FlowOriginKind.Host);
        Assert.True(table.TryClaim(key, key.Remote, 0, Endpoint.From(IPAddress.Loopback, 40000), forwardLocalAddress: null, DateTimeOffset.UtcNow, out var association));
        return association!;
    }

    private sealed class CountingRelay(Func<bool>? isRetired = null) : ITcpRelay
    {
        private int _disposeCount;

        public Task Completion => Task.CompletedTask;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public bool RetiredAtDispose { get; private set; }

        public ValueTask DisposeAsync()
        {
            RetiredAtDispose = isRetired?.Invoke() ?? false;
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
