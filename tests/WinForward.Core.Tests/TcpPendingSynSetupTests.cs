using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.Core.Tests.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

/// <summary>
/// R8 pending-SYN infrastructure bounds: the entry cap and global byte budget reject with a
/// trace (backpressure, not a cooldown), every retained copy is credited against the budget
/// exactly once across overwrite / TTL-expiry / completion sinks, the retention TTL rides the
/// idle sweep, and the setup-failure cooldown consumes retransmissions then self-prunes.
/// </summary>
public sealed class TcpPendingSynSetupTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, null, null);
    private static readonly IPAddress s_client = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destination = IPAddress.Parse("192.0.2.53");

    private static FlowKey Key(ushort port) => FlowKey.Create(Endpoint.From(s_client, port), Endpoint.From(s_destination, 443), TransportProtocol.Tcp, FlowOriginKind.Host);

    private static FlowContext Context(FlowKey key) => new(key, null, null, null, null, key.Remote.Port);

    /// <summary>
    /// A factory whose allocations park until released OR the token fires — unlike the shared
    /// <see cref="GatedListenerFactory"/>, which holds through cancellation so tests can prove
    /// dispose waits for an in-flight setup. The cap and sweep tests park many setups and end by
    /// disposing, so their gate must let shutdown cancellation unwind the park.
    /// </summary>
    private sealed class CancellableGatedListenerFactory : ITcpRedirectListenerFactory
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<FakeListener> _listeners = [];

        public TaskCompletionSource CreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<FakeListener> Listeners => _listeners;

        public async ValueTask<ITcpRedirectListener> CreateAsync(AddressFamilyKind addressFamily, CancellationToken cancellationToken)
        {
            CreateStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var address = addressFamily == AddressFamilyKind.IPv4 ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            var listener = new FakeListener(Endpoint.From(address, 42000));
            lock (_listeners) _listeners.Add(listener);
            return listener;
        }

        public void Release() => _release.TrySetResult();
    }

    private static bool TryRetain(TcpPendingSynSetupIndex index, FlowKey key, int byteCount, DateTimeOffset now, out PendingSynSetup? created) =>
        index.TryRetain(key, new byte[byteCount], Context(key), new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, 0x1234), 1, 1, now, out created);

    [Fact]
    public void EntryCapRejectsAndTracesAsBackpressure()
    {
        var index = new TcpPendingSynSetupIndex(capacity: 2);
        var now = DateTimeOffset.UtcNow;

        Assert.True(TryRetain(index, Key(53000), 10, now, out _));
        Assert.True(TryRetain(index, Key(53001), 10, now, out _));
        Assert.False(TryRetain(index, Key(53002), 10, now, out var created));

        Assert.Null(created);
        Assert.Equal(2, index.ActiveCount);
        Assert.Equal(1, index.RejectionCount);
    }

    [Fact]
    public void ByteBudgetRejectsAndKeepsOlderRetainOnOverwriteFailure()
    {
        var index = new TcpPendingSynSetupIndex(byteBudget: 24);
        var now = DateTimeOffset.UtcNow;
        var key = Key(53000);

        Assert.True(TryRetain(index, key, 10, now, out _));
        // An overwrite that does not fit is refused: the older copy and its running setup stay.
        Assert.False(TryRetain(index, key, 20, now, out var overwrite));
        Assert.Null(overwrite);
        Assert.Equal(10, index.ChargedBytes);
        Assert.Equal(1, index.RejectionCount);
        Assert.Equal(1, index.ActiveCount);
    }

    [Fact]
    public void OverwriteCreditsOlderCopyExactlyOnce()
    {
        var index = new TcpPendingSynSetupIndex();
        var now = DateTimeOffset.UtcNow;
        var key = Key(53000);

        Assert.True(TryRetain(index, key, 10, now, out var first));
        Assert.Equal(10, index.ChargedBytes);

        // A retransmission overwrites the retained copy: the older charge is credited, the
        // newest copy charged, and no second entry (no second setup task) is created.
        Assert.True(TryRetain(index, key, 20, now, out var overwrite));
        Assert.Null(overwrite);
        Assert.Equal(20, index.ChargedBytes);
        Assert.Equal(1, index.ActiveCount);

        // Completion credits the newest copy exactly once; the older copy's charge already
        // landed at the overwrite (ReferenceEquals guard is the completing entry itself).
        index.Complete(key, first!, writeCooldown: false, now);
        Assert.Equal(0, index.ChargedBytes);
        Assert.Equal(0, index.ActiveCount);
    }

    [Fact]
    public void TtlExpiryReclaimsStuckEntriesAndCreditsOnce()
    {
        var index = new TcpPendingSynSetupIndex();
        var now = DateTimeOffset.UtcNow;

        Assert.True(TryRetain(index, Key(53000), 10, now, out var entry));
        index.RemoveExpired(now.AddSeconds(6));

        Assert.Equal(0, index.ActiveCount);
        Assert.Equal(0, index.ChargedBytes);
        Assert.Equal(1, index.TtlExpiredCount);

        // The still-running setup's later Complete is a no-op: the credit landed exactly once
        // at the TTL removal (ReferenceEquals guard against the removed generation).
        index.Complete(Key(53000), entry!, writeCooldown: false, now.AddSeconds(7));
        Assert.Equal(0, index.ChargedBytes);
    }

    [Fact]
    public void CooldownConsumesRetransmissionsThenSelfPrunes()
    {
        var index = new TcpPendingSynSetupIndex();
        var now = DateTimeOffset.UtcNow;
        var key = Key(53000);

        Assert.True(TryRetain(index, key, 10, now, out var entry));
        index.Complete(key, entry!, writeCooldown: true, now);

        Assert.Equal(1, index.CooldownCount);
        Assert.True(index.IsInSetupCooldown(key, now.AddMilliseconds(500)));

        // The sweep prunes elapsed cooldowns, so a later SYN starts a fresh setup generation.
        index.RemoveExpired(now.AddSeconds(2));
        Assert.Equal(0, index.CooldownCount);
        Assert.False(index.IsInSetupCooldown(key, now.AddSeconds(2)));
        Assert.True(TryRetain(index, key, 10, now.AddSeconds(2), out var reentry));
        Assert.NotNull(reentry);
    }

    [Fact]
    public void DisposeDrainCreditsEveryRetainedCopy()
    {
        var index = new TcpPendingSynSetupIndex();
        var now = DateTimeOffset.UtcNow;

        Assert.True(TryRetain(index, Key(53000), 10, now, out _));
        Assert.True(TryRetain(index, Key(53001), 20, now, out _));
        Assert.Equal(30, index.ChargedBytes);

        index.RemoveAll();

        Assert.Equal(0, index.ActiveCount);
        Assert.Equal(0, index.ChargedBytes);
    }

    [Fact]
    public async Task CoordinatorCapRejectionFailsClosedWithTrace()
    {
        var listenerFactory = new CancellableGatedListenerFactory();
        var logger = new RecordingRuntimeLogger();
        var table = new TcpRedirectTable();
        await using var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), new FakeInjector(), table, new SelfTrafficRegistry(), new FakeLocalAddressProvider(), logger);

        // Every launched setup parks inside the gated factory, so entries accumulate to the cap.
        for (var port = 53000; port < 53000 + 1024; port++)
        {
            var outcome = await coordinator.HandleSynAsync(MakeSynPacket(s_client, s_destination, checked((ushort)port), 443), s_server, CancellationToken.None);
            Assert.Equal(TcpRedirectOutcome.SetupPending, outcome);
        }

        var rejected = await coordinator.HandleSynAsync(MakeSynPacket(s_client, s_destination, 61000, 443), s_server, CancellationToken.None);

        Assert.Equal(TcpRedirectOutcome.Blocked, rejected);
        Assert.Contains(logger.Events, e => string.Equals(e.Name, "tcp.setup.pending.dropped", StringComparison.Ordinal)
            && e.Fields.Any(field => string.Equals(field.Key, "reason", StringComparison.Ordinal) && field.Value is "pendingBudget"));
        Assert.Equal(TcpPendingSynSetupIndex.DefaultCapacity, coordinator.PendingSetups.ActiveCount);
        await coordinator.DisposeAsync();
        Assert.Equal(0, coordinator.PendingSetups.ActiveCount);
        Assert.Equal(0, coordinator.PendingSetups.ChargedBytes);
    }

    [Fact]
    public async Task SynDispatchDoesNotWaitForListenerAllocation()
    {
        // R8's core contract: the pump-side SYN dispatch completes while the listener factory is
        // still parked — the historical inline setup would have blocked on the bind forever.
        var listenerFactory = new GatedListenerFactory();
        var table = new TcpRedirectTable();
        var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), new FakeInjector(), table, new SelfTrafficRegistry(), new FakeLocalAddressProvider());

        var dispatch = coordinator.HandleSynAsync(MakeSynPacket(s_client, s_destination, 53000, 443), s_server, CancellationToken.None).AsTask();
        await dispatch.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TcpRedirectOutcome.SetupPending, dispatch.Result);
        // The background setup reached the factory only after the dispatch returned; the gate is
        // still closed, proving nothing on the dispatch path waited for the allocation.
        await listenerFactory.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, coordinator.PendingSetups.ActiveCount);

        listenerFactory.Release();
        await coordinator.DrainPendingSetupsAsync();
        Assert.Single(listenerFactory.Listeners);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task SweepExpiresStuckPendingEntryWhileSetupRemainsInFlight()
    {
        // The retention TTL rides the idle sweep: an entry whose setup never completed (parked
        // factory) is reclaimed and credited; the running setup is unaffected and still injects
        // its launch-time copy, and dispose afterwards leaves zero charge and no cooldown.
        var listenerFactory = new CancellableGatedListenerFactory();
        var table = new TcpRedirectTable();
        var coordinator = new TcpProxyCoordinator(listenerFactory, new FakeRelayFactory(), new FakeInjector(), table, new SelfTrafficRegistry(), new FakeLocalAddressProvider());

        await coordinator.HandleSynAsync(MakeSynPacket(s_client, s_destination, 53000, 443), s_server, CancellationToken.None);
        await listenerFactory.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var chargedAtPark = coordinator.PendingSetups.ChargedBytes;
        Assert.True(chargedAtPark > 0);

        await coordinator.RemoveExpiredAsync(DateTimeOffset.UtcNow.AddSeconds(6), TimeSpan.FromMinutes(1));

        Assert.Equal(0, coordinator.PendingSetups.ActiveCount);
        Assert.Equal(0, coordinator.PendingSetups.ChargedBytes);
        Assert.Equal(1, coordinator.PendingSetups.TtlExpiredCount);

        listenerFactory.Release();
        await coordinator.DrainPendingSetupsAsync();
        // The setup completed against its launch-time copy: exactly one rewritten SYN injected
        // and one association registered, despite the entry having been reclaimed mid-flight.
        Assert.Single(table.Snapshot());
        await coordinator.DisposeAsync();
        Assert.Equal(0, coordinator.PendingSetups.ChargedBytes);
        Assert.Equal(0, coordinator.PendingSetups.CooldownCount);
    }
}
