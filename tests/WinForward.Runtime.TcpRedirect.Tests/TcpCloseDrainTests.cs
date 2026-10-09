using System.Buffers.Binary;
using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.FrameBuilders;
using static WinForward.TestSupport.TcpCoordinatorFakes;

namespace WinForward.Runtime.TcpRedirect.Tests;

/// <summary>
/// The bounded close drain: a clean relay end disposes the relay first, so MSTCP's graceful close
/// emits the real, retransmittable FIN while the association still claims the tuple, and the retire
/// waits for the client's acknowledgement (or the deadline, or another teardown path) before it runs.
/// The drain never moves the retire itself: the store's single atomic critical section is untouched.
/// </summary>
public sealed class TcpCloseDrainTests
{
    private static readonly IPAddress s_clientIPv4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destIPv4 = IPAddress.Parse("192.0.2.53");
    private static readonly NativeBufferPool s_synCopyPool = new(NdisApiAbi.MaximumEthernetFrame);
    private static readonly Socks5Server s_server = new("primary", "127.0.0.1", 1080, Username: null, Password: null);
    private const long DeliveredBytes = 5;
    // Deadlines: the unreachable one cannot elapse inside WaitForAsync's own budget, so a drain that
    // ends while it is armed can only have been ended by the acknowledgement it observes; the bounded
    // one is the fallback exit driven by a test that never acknowledges.
    private static readonly TimeSpan s_unreachableDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_boundedDeadline = TimeSpan.FromMilliseconds(150);

    [Fact]
    public async Task CleanEndDrainsUntilTheClientAcknowledgesThenRetires()
    {
        var order = new StepLog();
        var relay = new CleanEndRelay(DeliveredBytes, order);
        var (acceptor, session, listener, logger) = CreateAcceptor(relay, order, s_unreachableDeadline);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(session.Association.AcceptedPeerEndpoint), CancellationToken.None);
        var acceptLoop = acceptor.RunAcceptLoopAsync(session);

        relay.Complete();
        await WaitForAsync(() => session.Association.Phase == RelayPhase.Draining);

        // The relay was disposed before the wait armed — that close is what emits the FIN — and the
        // retire has not run: the still-registered alias is carrying the close.
        Assert.Equal(["dispose"], order.Steps);
        Assert.Equal(1, relay.DisposeCount);

        ObserveClientAck(session.Association, DrainTargetAck(session.Association));
        await WaitForAsync(() => order.Contains("teardown"));

        Assert.Equal(["dispose", "teardown"], order.Steps);
        Assert.Equal("acknowledged", DrainOutcome(logger));
        session.Retire();
        await acceptLoop;
    }

    [Fact]
    public async Task CleanEndWithoutAnAcknowledgementRetiresAtTheDeadline()
    {
        var order = new StepLog();
        var relay = new CleanEndRelay(DeliveredBytes, order);
        var (acceptor, session, listener, logger) = CreateAcceptor(relay, order, s_boundedDeadline);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(session.Association.AcceptedPeerEndpoint), CancellationToken.None);
        var acceptLoop = acceptor.RunAcceptLoopAsync(session);

        relay.Complete();
        await WaitForAsync(() => order.Contains("teardown"));

        Assert.Equal(["dispose", "teardown"], order.Steps);
        Assert.Equal("deadline", DrainOutcome(logger));
        session.Retire();
        await acceptLoop;
    }

    [Fact]
    public async Task CleanEndWithoutObservedSequencesRetiresImmediatelyWithoutADrain()
    {
        var order = new StepLog();
        var relay = new CleanEndRelay(DeliveredBytes, order);
        var injector = new StepInjector(order);
        // The deadline is far beyond the wait budget on purpose: a drain would pin this test out.
        var (acceptor, session, listener, logger) = CreateAcceptor(relay, order, s_unreachableDeadline, observedSequences: false, injector: injector);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(session.Association.AcceptedPeerEndpoint), CancellationToken.None);
        var acceptLoop = acceptor.RunAcceptLoopAsync(session);

        relay.Complete();
        await WaitForAsync(() => order.Contains("teardown"));

        // No template and no listener-side ISN: no computable target, no injection, no early dispose,
        // no drain — the retire is immediate.
        Assert.Empty(injector.Frames);
        Assert.Equal(["teardown"], order.Steps);
        Assert.Equal(0, relay.DisposeCount);
        Assert.NotEqual(RelayPhase.Draining, session.Association.Phase);
        Assert.DoesNotContain(logger.Events, recorded => string.Equals(recorded.Name, "tcp.redirect.drain", StringComparison.Ordinal));
        session.Retire();
        await acceptLoop;
    }

    [Fact]
    public async Task CleanEndWithoutEndInfoRetiresImmediatelyWithoutADrain()
    {
        var order = new StepLog();
        var relay = new EndInfoLessRelay();
        var (acceptor, session, listener, logger) = CreateAcceptor(relay, order, s_unreachableDeadline);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(session.Association.AcceptedPeerEndpoint), CancellationToken.None);
        var acceptLoop = acceptor.RunAcceptLoopAsync(session);

        relay.Complete();
        await WaitForAsync(() => order.Contains("teardown"));

        // A relay without the end-info capability is treated as a clean end, but its delivered byte
        // count is unknown, so the drain degrades and the retire is immediate.
        Assert.Equal(["teardown"], order.Steps);
        Assert.DoesNotContain(logger.Events, recorded => string.Equals(recorded.Name, "tcp.redirect.drain", StringComparison.Ordinal));
        session.Retire();
        await acceptLoop;
    }

    [Fact]
    public async Task CleanEndInjectsNoCraftedPacketAndDrainsUntilTheClientAcknowledges()
    {
        var order = new StepLog();
        var relay = new CleanEndRelay(DeliveredBytes, order);
        var injector = new StepInjector(order);
        var (acceptor, session, listener, logger) = CreateAcceptor(relay, order, s_unreachableDeadline, injector: injector);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(session.Association.AcceptedPeerEndpoint), CancellationToken.None);
        var acceptLoop = acceptor.RunAcceptLoopAsync(session);

        relay.Complete();
        await WaitForAsync(() => session.Association.Phase == RelayPhase.Draining);

        // The close is MSTCP's own FIN, emitted by the relay disposal; nothing is crafted from the
        // recorded SYN template, and the drain still runs on the client's acknowledgement.
        Assert.Empty(injector.Frames);
        Assert.Equal(["dispose"], order.Steps);

        ObserveClientAck(session.Association, DrainTargetAck(session.Association));
        await WaitForAsync(() => order.Contains("teardown"));

        Assert.Empty(injector.Frames);
        Assert.Equal(["dispose", "teardown"], order.Steps);
        Assert.Equal("acknowledged", DrainOutcome(logger));
        session.Retire();
        await acceptLoop;
    }

    [Fact]
    public async Task TheRetireNeverPrecedesTheDrainExit()
    {
        var order = new StepLog();
        var relay = new CleanEndRelay(DeliveredBytes, order);
        var table = new TcpRedirectTable(capacity: 8);
        var logger = new RecordingLogger();
        var store = new TcpRedirectSessionStore(table, logger, capacity: 8, TimeProvider.System);
        var session = CreateSession(out var listener, observedSequences: true);
        Assert.Same(session, store.TryRegister(session));
        // The acceptor's own attach is what gives the store the relay to release: pre-attaching
        // here would make that attach fail and the relay would take the discarded-attach path.
        var acceptor = new TcpRedirectAcceptor(
            new InlineRelayFactory(relay),
            logger,
            new ClientResetInjector(new StepInjector(order), logger, store.TearDownSessionAsync, store.FailAssociationAsync),
            tryAttachRelay: store.TryAttachRelay,
            tearDownSession: store.TearDownSessionAsync,
            drainDeadline: s_unreachableDeadline);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(session.Association.AcceptedPeerEndpoint), CancellationToken.None);
        var acceptLoop = acceptor.RunAcceptLoopAsync(session);

        relay.Complete();
        await WaitForAsync(() => session.Association.Phase == RelayPhase.Draining);
        Assert.False(session.IsRetired);

        ObserveClientAck(session.Association, DrainTargetAck(session.Association));
        await WaitForAsync(() => EventIndex(logger, "tcp.redirect.closed") >= 0);

        var drainIndex = EventIndex(logger, "tcp.redirect.drain");
        var closedIndex = EventIndex(logger, "tcp.redirect.closed");
        Assert.True(drainIndex >= 0, "the drain event was never recorded");
        Assert.True(drainIndex < closedIndex, "the drain must exit before the retire, so its record precedes the closed record");
        // One retire, one closed record: the acceptor's earlier relay disposal is the same
        // single-flight teardown the store's release joins, so nothing is released twice.
        Assert.Equal(1, CountEvents(logger, "tcp.redirect.closed"));
        await acceptLoop;
    }

    [Fact]
    public async Task AStragglerDuringTheDrainResolvesToTheSameAssociationAndArmsNoSetup()
    {
        var harness = CreateDispatcherHarness();
        await using var coordinator = harness.Coordinator;
        await EstablishRelayingSessionAsync(harness);
        var association = Assert.Single(harness.Table.Snapshot());
        harness.Injector.InjectedFrames.Clear();

        // Armed against a target the straggler's acknowledgement does not cover, so the association
        // stays draining while the packet is routed — the state the real drain is in.
        var drain = association.ArmDrainAsync(0x4000_0000);
        Assert.False(drain.IsCompleted);
        Assert.Equal(RelayPhase.Draining, association.Phase);

        await harness.Dispatcher.DispatchAsync(MakeForwardTcpPacket(s_clientIPv4, s_destIPv4, 53000, 443, TcpFlagAck, payload: [1, 2, 3]), CancellationToken.None);

        Assert.Single(harness.Injector.InjectedFrames);
        Assert.Equal(RelayPhase.Draining, association.Phase);
        Assert.Single(harness.ListenerFactory.Listeners);
        Assert.Equal(1, harness.Table.Count);
        Assert.Equal(1, coordinator.SessionCount);
        Assert.False(coordinator.Tombstones.TryHit(association.OriginalKey, DateTimeOffset.UtcNow));

        // The reverse leg's retransmitted FIN resolves through the reverse index to the same
        // association and is rewritten toward the client.
        var listenerTuple = Assert.Single(harness.ListenerFactory.Listeners).TranslatedTuple;
        var reverse = MakeReversePacketClassifierOrientation(s_clientIPv4, listenerTuple.Port, s_destIPv4, 53000);
        Assert.Equal(TcpRedirectOutcome.Injected, await HandleReverseAsync(coordinator, harness.Table, reverse, CancellationToken.None));

        Assert.Equal(2, harness.Injector.InjectedFrames.Count);
        Assert.Equal(RelayPhase.Draining, association.Phase);
        Assert.Equal(1, harness.Table.Count);
    }

    [Fact]
    public async Task AForwardAckThatCoversTheCloseCompletesTheArmedDrain()
    {
        const uint targetAck = 0x4000_0000;
        var harness = CreateDispatcherHarness();
        await using var coordinator = harness.Coordinator;
        await EstablishRelayingSessionAsync(harness);
        var association = Assert.Single(harness.Table.Snapshot());
        harness.Injector.InjectedFrames.Clear();
        var drain = association.ArmDrainAsync(targetAck);

        // The real forward path carries the acknowledgement: the dispatcher resolves the flow and the
        // coordinator reads the ACK field of the pre-rewrite frame, which completes the drain.
        await harness.Dispatcher.DispatchAsync(
            MakeForwardTcpPacket(s_clientIPv4, s_destIPv4, 53000, 443, TcpFlagAck, payload: [1, 2, 3], mutateFrame: frame => BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(42, 4), targetAck)),
            CancellationToken.None);

        Assert.True(drain.IsCompletedSuccessfully);
        Assert.Equal(RelayPhase.Draining, association.Phase);
        Assert.Equal(1, harness.Table.Count);
        Assert.Equal(1, coordinator.SessionCount);
        Assert.Single(harness.ListenerFactory.Listeners);
        Assert.Single(harness.Injector.InjectedFrames);
    }

    [Fact]
    public void ArmingADrainNeverRewritesAClosingPhase()
    {
        var association = CreateHostAssociation(Endpoint.From(IPAddress.Loopback, 40_000));
        // Another teardown path retired first; the arm must not move the phase back.
        association.Phase = RelayPhase.Closing;

        var drain = association.ArmDrainAsync(0x4000_0000);

        Assert.False(drain.IsCompleted);
        Assert.Equal(RelayPhase.Closing, association.Phase);
    }

    [Fact]
    public async Task AnotherRetirePathEndsTheDrainImmediately()
    {
        var order = new StepLog();
        var relay = new CleanEndRelay(DeliveredBytes, order);
        var (acceptor, session, listener, logger) = CreateAcceptor(relay, order, s_unreachableDeadline);
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(session.Association.AcceptedPeerEndpoint), CancellationToken.None);
        var acceptLoop = acceptor.RunAcceptLoopAsync(session);

        relay.Complete();
        await WaitForAsync(() => session.Association.Phase == RelayPhase.Draining);

        // An injection failure, a fragment, capacity, the sweep and shutdown all funnel through
        // Retire(), which cancels the token the drain's wait is bound to.
        session.Retire();
        await WaitForAsync(() => order.Contains("teardown"));

        Assert.Equal(["dispose", "teardown"], order.Steps);
        Assert.Equal("retired", DrainOutcome(logger));
        await acceptLoop;
    }

    [Fact]
    public async Task APiggybackedAcknowledgementEndsTheDrainWithoutWaiting()
    {
        var order = new StepLog();
        var relay = new CleanEndRelay(DeliveredBytes, order);
        var (acceptor, session, listener, logger) = CreateAcceptor(relay, order, s_unreachableDeadline);

        // The client acknowledges our FIN on its own FIN before the relay ends: the tracker must
        // already cover the target when the drain arms, so arming exits without a wait.
        ObserveClientAck(session.Association, DrainTargetAck(session.Association));
        await listener.AcceptChannel.Writer.WriteAsync(new FakeAcceptedConnection(session.Association.AcceptedPeerEndpoint), CancellationToken.None);
        var acceptLoop = acceptor.RunAcceptLoopAsync(session);

        relay.Complete();
        await WaitForAsync(() => order.Contains("teardown"));

        Assert.Equal(["dispose", "teardown"], order.Steps);
        Assert.Equal("acknowledged", DrainOutcome(logger));
        session.Retire();
        await acceptLoop;
    }

    [Fact]
    public void ClientAckTrackingIsAdvanceOnlyWrapsAndIgnoresFramesWithoutTheAckFlag()
    {
        var association = CreateHostAssociation(Endpoint.From(IPAddress.Loopback, 40_000));
        Assert.Null(association.ClientAckMax);

        ObserveClientAck(association, 100);
        Assert.Equal(100u, association.ClientAckMax);

        ObserveClientAck(association, 50);
        Assert.Equal(100u, association.ClientAckMax);

        // Serial arithmetic, not an unsigned comparison: 0xFFFF_FF00 is 256 behind 100.
        ObserveClientAck(association, 0xFFFF_FF00);
        Assert.Equal(100u, association.ClientAckMax);

        // ... and the boundary itself advances: past a tracker just below it, 0x0000_0100 is ahead.
        var wrapped = CreateHostAssociation(Endpoint.From(IPAddress.Loopback, 40_001));
        ObserveClientAck(wrapped, 0xFFFF_FF00);
        ObserveClientAck(wrapped, 0x0000_0100);
        Assert.Equal(0x0000_0100u, wrapped.ClientAckMax);

        // A frame without the ACK control bit carries no acknowledgement value; a SYN's field is
        // zero, and a tracked zero would look like it covers a target in the sequence space's upper
        // half — an arm that exits before the close was ever acknowledged.
        var syn = BuildIPv4TcpSyn(s_clientIPv4, s_destIPv4, 53000, 443);
        TcpSequenceObservation.TrackClientAck(syn, LayoutOf(syn), association);
        Assert.Equal(100u, association.ClientAckMax);
    }

    /// <summary>
    /// The acknowledgement that covers the close: the listener-side ISN plus one for the SYN-ACK,
    /// the bytes the relay delivered, and one for the FIN.
    /// </summary>
    private static uint DrainTargetAck(TcpRedirectAssociation association) =>
        association.ServerInitialSeq!.Value + 2 + (uint)DeliveredBytes;

    private static void ObserveClientAck(TcpRedirectAssociation association, uint acknowledgement)
    {
        var frame = BuildIPv4TcpFrame(s_clientIPv4, s_destIPv4, 53000, 443, tcpFlags: TcpFlagAck);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(42, 4), acknowledgement);
        TcpSequenceObservation.TrackClientAck(frame, LayoutOf(frame), association);
    }

    private static string DrainOutcome(RecordingLogger logger) =>
        (string)Assert.Single(logger.Events, recorded => string.Equals(recorded.Name, "tcp.redirect.drain", StringComparison.Ordinal)).Field("Outcome")!;

    private static int CountEvents(RecordingLogger logger, string eventName) =>
        logger.Events.Count(recorded => string.Equals(recorded.Name, eventName, StringComparison.Ordinal));

    private static int EventIndex(RecordingLogger logger, string eventName)
    {
        var events = logger.Events;
        for (var index = 0; index < events.Count; index++)
        {
            if (string.Equals(events[index].Name, eventName, StringComparison.Ordinal)) return index;
        }

        return -1;
    }

    private static (TcpRedirectAcceptor Acceptor, TcpRedirectSession Session, FakeListener Listener, RecordingLogger Logger) CreateAcceptor(
        ITcpRelay relay,
        StepLog order,
        TimeSpan drainDeadline,
        bool observedSequences = true,
        StepInjector? injector = null)
    {
        var session = CreateSession(out var listener, observedSequences);
        var logger = new RecordingLogger();
        var acceptor = new TcpRedirectAcceptor(
            new InlineRelayFactory(relay),
            logger,
            new ClientResetInjector(injector ?? new StepInjector(order), logger, static _ => ValueTask.CompletedTask, static _ => ValueTask.CompletedTask),
            tryAttachRelay: static (_, _) => true,
            tearDownSession: _ =>
            {
                order.Add("teardown");
                return ValueTask.CompletedTask;
            },
            drainDeadline: drainDeadline);
        return (acceptor, session, listener, logger);
    }

    /// <summary>
    /// A host-shape session with the close inputs: the original SYN template, both ISNs, and no
    /// tracked advancement beyond them.
    /// </summary>
    private static TcpRedirectSession CreateSession(out FakeListener listener, bool observedSequences)
    {
        var key = FlowKey.Create(Endpoint.From(s_clientIPv4, 53000), Endpoint.From(s_destIPv4, 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        var association = new TcpRedirectAssociation(key, key.Remote, 0x1234, Endpoint.From(IPAddress.Loopback, 40_000), forwardLocalAddress: null, 1, DateTimeOffset.UtcNow);
        if (observedSequences)
        {
            TcpSequenceObservation.RecordClientSyn(BuildIPv4TcpSyn(s_clientIPv4, s_destIPv4, 53000, 443), association, s_synCopyPool);
            var synAck = BuildIPv4TcpSyn(s_destIPv4, s_clientIPv4, 443, 53000);
            synAck[47] = 0x12;
            TcpSequenceObservation.RecordServerSynAck(synAck, association);
        }

        listener = new FakeListener(association.TranslatedListenerTuple);
        var token = new SelfTrafficRegistry().Register(new SelfTrafficRegistry.SelfTrafficKey(TransportProtocol.Tcp, association.TranslatedListenerTuple, association.TranslatedListenerTuple));
        return new TcpRedirectSession(association, listener, token, s_server, 0, CancellationToken.None);
    }

    /// <summary>
    /// A thread-safe ordered step log: the acceptor, the relay and the teardown run on different
    /// threads, so an unsynchronised list would race the reader's snapshot.
    /// </summary>
    private sealed class StepLog
    {
        private readonly List<string> _steps = [];

        public IReadOnlyList<string> Steps
        {
            get
            {
                lock (_steps) return [.. _steps];
            }
        }

        public void Add(string step)
        {
            lock (_steps) _steps.Add(step);
        }

        public bool Contains(string step)
        {
            lock (_steps) return _steps.Contains(step);
        }
    }

    /// <summary>A clean relay end that reports its delivered byte count and records the disposal the
    /// drain performs before it arms.</summary>
    private sealed class CleanEndRelay(long serverStreamBytes, StepLog order) : ITcpRelay, ITcpRelayEndInfo
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        public Task Completion => _completion.Task;
        public RelayEndKind EndKind => RelayEndKind.CleanEnded;
        public long ServerStreamBytes { get; } = serverStreamBytes;
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Complete() => _completion.TrySetResult();

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            order.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A relay without the end-info capability: a clean end with no delivered byte count.</summary>
    private sealed class EndInfoLessRelay : ITcpRelay
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => _completion.Task;

        public void Complete() => _completion.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InlineRelayFactory(ITcpRelay relay) : ITcpProxyRelayFactory
    {
        public ValueTask<ITcpRelay> EstablishAsync(Endpoint originalDestination, ITcpAcceptedConnection acceptedConnection, Socks5Server server, CancellationToken cancellationToken)
            => ValueTask.FromResult(relay);
    }

    /// <summary>An injector that records the injection step and each crafted frame, so the tests can
    /// pin what ran, when, and whether anything was crafted at all.</summary>
    private sealed class StepInjector(StepLog order) : ITcpRedirectInjector
    {
        public List<byte[]> Frames { get; } = [];

        public ValueTask InjectAsync(ReadOnlyMemory<byte> rewrittenFrame, bool towardMstcp, nint adapterHandle, CancellationToken cancellationToken)
        {
            lock (Frames) Frames.Add(rewrittenFrame.ToArray());
            order.Add("inject");
            return ValueTask.CompletedTask;
        }

        public void Inject(NdisPacketBuffer stagedFrame, bool towardMstcp, nint adapterHandle, CancellationToken cancellationToken)
        {
            lock (Frames) Frames.Add(stagedFrame.GetFrame().ToArray());
            order.Add("inject");
        }

        public void InjectBatch(NdisPacketBuffer[] frames, int count, bool towardMstcp, nint adapterHandle)
        {
            lock (Frames)
            {
                for (var index = 0; index < count; index++) Frames.Add(frames[index].GetFrame().ToArray());
            }

            order.Add("inject");
        }
    }
}
