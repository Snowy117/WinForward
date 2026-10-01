using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.FrameBuilders;
using static WinForward.TestSupport.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

/// <summary>
/// Lane-batched redirect injection (task 09-29-tcp-redirect-batched-injection): the two mid-flow
/// redirect data legs accumulate rewritten frames in per-(adapter handle, target direction) lanes
/// and one pump-iteration flush sends each lane as a single batched reinjection call; the immediate
/// single send survives as the control-frame, non-pump-packet, and lane-overflow path. Pinned here:
/// one batch per (adapter, direction) per iteration in append order, both host legs sharing the
/// toward-MSTCP lane, control frames staying immediate and ordered before the flush, an
/// all-or-nothing batch failure degrading to per-frame sends with each frame's own failure tail
/// (and no rental leak or double release), lane overflow falling back to the immediate send, and a
/// reconstructed non-pump packet never entering a lane.
/// </summary>
public sealed class TcpRedirectInjectionBatchingTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);
    private static readonly IPAddress s_clientIpv4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destIpv4 = IPAddress.Parse("192.0.2.53");

    /// <summary>The enumeration handle the shared SYN fakes carry; both data legs key their lanes on it.</summary>
    private const nint AdapterHandle = 0x1234;

    /// <summary>IPv4 TCP data frame with a one-byte payload: 14 Ethernet + 20 IPv4 + 20 TCP + 1.</summary>
    private const int DataFrameLength = 55;

    [Fact]
    public async Task FullBatchOfDataFramesLeavesAsOneBatchInAppendOrder()
    {
        await using var harness = new RedirectHarness();
        await harness.EstablishHostRedirectAsync(53000);

        // A full 32-frame pump batch: one batched call replaces 32 single sends (the >= 10x call
        // reduction acceptance) and the frames keep capture order.
        const int frames = 32;
        for (byte marker = 1; marker <= frames; marker++) await harness.DispatchForwardDataAsync(53000, marker);

        Assert.Equal(frames, harness.Coordinator.Diagnostics.RedirectPendingCount);
        Assert.Empty(harness.Injector.SingleCalls);
        Assert.Empty(harness.Injector.BatchCalls);

        harness.Coordinator.FlushPendingRedirectInjections(AdapterHandle);

        var batch = Assert.Single(harness.Injector.BatchCalls);
        Assert.Equal(AdapterHandle, batch.Handle);
        Assert.True(batch.TowardMstcp);
        for (var index = 0; index < frames; index++) Assert.Equal((byte)(index + 1), batch.Frames[index][^1]);
        Assert.Equal(0, harness.Coordinator.Diagnostics.RedirectPendingCount);
        Assert.Equal(0L, harness.Coordinator.Diagnostics.RedirectDegradedFlushCount);
    }

    [Fact]
    public async Task HostForwardAndReverseLegsShareTheTowardMstcpLaneInCaptureOrder()
    {
        await using var harness = new RedirectHarness();
        var listener = await harness.EstablishHostRedirectAsync(53000);
        var reverseSource = Endpoint.From(s_clientIpv4, listener.Port);
        var reverseDestination = Endpoint.From(s_destIpv4, 53000);

        // Capture order interleaves the two host legs: the client's forward data and the listener's
        // reversed replies both target MSTCP on the same adapter, so they share one lane and leave
        // in the order they were captured.
        await harness.DispatchForwardDataAsync(53000, 1);
        await harness.DispatchReverseDataAsync(reverseSource, reverseDestination, 2);
        await harness.DispatchForwardDataAsync(53000, 3);
        await harness.DispatchReverseDataAsync(reverseSource, reverseDestination, 4);

        harness.Coordinator.FlushPendingRedirectInjections(AdapterHandle);

        var batch = Assert.Single(harness.Injector.BatchCalls);
        Assert.True(batch.TowardMstcp);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, batch.Frames.Select(frame => frame[^1]).ToArray());
        Assert.Empty(harness.Injector.SingleCalls);
    }

    [Fact]
    public async Task ForwardedFlowReverseLegTargetsTheOriginAdapterLane()
    {
        var forwardLocal = IPAddress.Parse("192.0.2.1");
        const nint originHandle = 0x7777;
        await using var harness = new RedirectHarness(forwardLocal: forwardLocal);
        var listener = await harness.EstablishForwardedRedirectAsync(originHandle);
        var reverseSource = Endpoint.From(forwardLocal, listener.Port);
        var reverseDestination = Endpoint.From(s_clientIpv4, 53000);

        // A forwarded flow's client frame arrives on the origin adapter and goes toward MSTCP; its
        // reversed reply is emitted on that same origin adapter even when it was captured on another
        // one, so both lanes belong to the origin handle, not to the capture handle.
        await harness.DispatchForwardedDataAsync(53000, 1, originHandle);
        await harness.DispatchReverseDataAsync(reverseSource, reverseDestination, 2, adapterHandle: AdapterHandle);

        harness.Coordinator.FlushPendingRedirectInjections(originHandle);

        Assert.Equal(2, harness.Injector.BatchCalls.Count);
        var toMstcp = harness.Injector.BatchCalls.Single(call => call.TowardMstcp);
        var toAdapter = harness.Injector.BatchCalls.Single(call => !call.TowardMstcp);
        Assert.Equal(originHandle, toMstcp.Handle);
        Assert.Equal(originHandle, toAdapter.Handle);
        Assert.Equal(new byte[] { 1 }, toMstcp.Frames.Select(frame => frame[^1]).ToArray());
        Assert.Equal(new byte[] { 2 }, toAdapter.Frames.Select(frame => frame[^1]).ToArray());
        Assert.Empty(harness.Injector.SingleCalls);

        harness.Coordinator.FlushPendingRedirectInjections(AdapterHandle);
        Assert.Equal(2, harness.Injector.BatchCalls.Count);
    }

    [Fact]
    public async Task ReverseFrameForAnOriginAdapterOutsideTheScopeStaysImmediate()
    {
        var pool = new NdisPacketBufferPool(8);
        var forwardLocal = IPAddress.Parse("192.0.2.1");
        const nint originHandle = 0x7777;
        await using var harness = new RedirectHarness(failDataFrameSends: true, forwardLocal: forwardLocal, pool: pool);
        var listener = await harness.EstablishForwardedRedirectAsync(originHandle);
        var reverseSource = Endpoint.From(forwardLocal, listener.Port);
        var reverseDestination = Endpoint.From(s_clientIpv4, 53000);

        // No pump drains a lane keyed on an adapter that left the scope, so the frame must keep the
        // immediate send: it fails on the stale handle and runs the per-flow tail, where a lane would
        // have held the frame, its rental, and the client-visible reset indefinitely.
        harness.UpdateRedirectTargets(AdapterHandle);
        Assert.Equal(TcpRedirectOutcome.Blocked, await harness.DispatchReverseDataAsync(reverseSource, reverseDestination, 1, adapterHandle: AdapterHandle));

        Assert.Empty(harness.Injector.BatchCalls);
        var failed = Assert.Single(harness.Injector.SingleCalls);
        Assert.False(failed.TowardMstcp);
        Assert.Equal(originHandle, failed.Handle);
        Assert.Contains(harness.Logger.Events, entry => string.Equals(entry.Name, "tcp.redirect.failed", StringComparison.Ordinal));
        Assert.Equal(0, harness.Coordinator.Diagnostics.RedirectPendingCount);
        Assert.Equal(0, harness.Table.Count);
        var stats = pool.Stats;
        Assert.Equal(stats.Rented, stats.Returned);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void LaneFrameCapNeverExceedsOneDriverSendChunk()
    {
        // The degraded retry re-sends every frame of a failed batch, which is duplicate-free only
        // while a batch never spanned more than one driver chunk.
        Assert.True(
            RedirectInjectionLanes.LaneFrameCapacity <= NdisApiDriver.MaxPacketsPerSendRequest,
            string.Create(CultureInfo.InvariantCulture, $"a redirect lane must fit one driver chunk: lane cap {RedirectInjectionLanes.LaneFrameCapacity} > chunk {NdisApiDriver.MaxPacketsPerSendRequest}"));
    }

    [Fact]
    public async Task CapacityResetControlFrameStaysImmediateAndPrecedesTheFlush()
    {
        await using var harness = new RedirectHarness(capacity: 1);
        await harness.EstablishHostRedirectAsync(53000);

        // The second flow hits the session capacity gate, so its SYN is answered immediately with a
        // crafted RST|ACK — control frames are never deferred, and dispatch always precedes the flush.
        Assert.Equal(TcpRedirectOutcome.Blocked, await harness.Coordinator.HandleSynAsync(MakeSynPacket(s_clientIpv4, s_destIpv4, 53001, 443), s_server, CancellationToken.None));
        await harness.DispatchForwardDataAsync(53000, 1);

        harness.Coordinator.FlushPendingRedirectInjections(AdapterHandle);

        Assert.Equal(
            [RedirectCallKind.Async, RedirectCallKind.Single, RedirectCallKind.Batch],
            [.. harness.Injector.Calls.Select(call => call.Kind)]);
        var reset = Assert.Single(harness.Injector.SingleCalls);
        Assert.Equal(AdapterHandle, reset.Handle);
        Assert.True(reset.TowardMstcp);
        Assert.Equal(0, harness.Coordinator.Diagnostics.RedirectDegradedFlushCount);
    }

    [Fact]
    public async Task FailedBatchDegradesToPerFrameSendsAndFailsEachAssociation()
    {
        var pool = new NdisPacketBufferPool(8);
        await using var harness = new RedirectHarness(throwOnBatch: true, failDataFrameSends: true, pool: pool);
        var listeners = new List<Endpoint>();
        for (ushort clientPort = 53000; clientPort < 53003; clientPort++) listeners.Add(await harness.EstablishHostRedirectAsync(clientPort));
        for (var index = 0; index < listeners.Count; index++) await harness.RecordServerSynAckAsync(listeners[index], (ushort)(53000 + index));
        for (var index = 0; index < 3; index++) await harness.DispatchForwardDataAsync((ushort)(53000 + index), (byte)(index + 1));

        // The batched ABI is all-or-nothing: the failed batch must not throw into the pump, and every
        // frame it carried is retried on its own.
        harness.Coordinator.FlushPendingRedirectInjections(AdapterHandle);

        Assert.Equal(3, harness.Injector.SingleCalls.Count(call => call.Failed));
        var resets = harness.Injector.SingleCalls.Where(call => !call.Failed && ResetDestinationPort(call) > 0);
        Assert.Equal(new ushort[] { 53000, 53001, 53002 }, resets.Select(ResetDestinationPort).Order());
        Assert.Equal(3, harness.Logger.Events.Count(entry => string.Equals(entry.Name, "tcp.redirect.failed", StringComparison.Ordinal)));
        Assert.Contains(harness.Logger.Events, entry => string.Equals(entry.Name, "tcp.redirect.batch-failed", StringComparison.Ordinal));
        Assert.Equal(1L, harness.Coordinator.Diagnostics.RedirectDegradedFlushCount);
        Assert.Equal(0, harness.Coordinator.Diagnostics.RedirectPendingCount);
        Assert.Equal(0, harness.Table.Count);
        var stats = pool.Stats;
        Assert.Equal(stats.Rented, stats.Returned);
        Assert.Equal(0L, stats.Outstanding);
    }

    [Fact]
    public async Task ThrowingFailureTailOnOneFrameStillDrainsTheRestOfTheLane()
    {
        var pool = new NdisPacketBufferPool(8);
        await using var harness = new RedirectHarness(throwOnBatch: true, failDataFrameSends: true, faultFailureTailWarn: true, pool: pool);
        var listeners = new List<Endpoint>();
        for (ushort clientPort = 53000; clientPort < 53003; clientPort++) listeners.Add(await harness.EstablishHostRedirectAsync(clientPort));
        for (var index = 0; index < listeners.Count; index++) await harness.RecordServerSynAckAsync(listeners[index], (ushort)(53000 + index));
        var rentedBeforeFrames = pool.Stats.Rented;
        for (var index = 0; index < 3; index++) await harness.DispatchForwardDataWithMaterializedLeaseAsync((ushort)(53000 + index), (byte)(index + 1));
        Assert.Equal(rentedBeforeFrames + 3, pool.Stats.Rented);

        harness.Coordinator.FlushPendingRedirectInjections(AdapterHandle);

        // Every frame was attempted; the first frame's faulting tail was contained and the two
        // remaining frames still reached their own tails. The one association still in the table is
        // the first frame's: its tail aborted at the injected warn, before the fail-closed write.
        Assert.Equal(3, harness.Injector.SingleCalls.Count(call => call.Failed));
        Assert.Equal(1, harness.Logger.Events.Count(entry => string.Equals(entry.Name, "tcp.redirect.deferred-failed", StringComparison.Ordinal)));
        Assert.Contains(harness.Logger.Lines, line => line.Level == RuntimeLogLevel.Warn && line.Message.Contains("keeps draining", StringComparison.Ordinal));
        Assert.Equal(1, harness.Table.Count);
        Assert.Equal(0, harness.Coordinator.Diagnostics.RedirectPendingCount);
        var stats = pool.Stats;
        Assert.Equal(stats.Rented, stats.Returned);
        Assert.Equal(0L, stats.Outstanding);
    }

    [Fact]
    public async Task LaneOverflowFallsBackToTheImmediateSendAndIsCounted()
    {
        await using var harness = new RedirectHarness();
        await harness.EstablishHostRedirectAsync(53000);

        // The lane table caps at 64 keys and grows by doubling: 64 distinct handles fill it, so the
        // 65th key cannot accumulate and its frame keeps today's immediate single send.
        const int laneCapacity = 64;
        for (var handle = 1; handle <= laneCapacity + 1; handle++) await harness.DispatchForwardDataAsync(53000, (byte)handle, handle);
        Assert.Equal(laneCapacity, harness.Coordinator.Diagnostics.RedirectPendingCount);
        Assert.Equal(1L, harness.Coordinator.Diagnostics.RedirectOverflowCount);
        var overflowed = Assert.Single(harness.Injector.SingleCalls);
        Assert.Equal(laneCapacity + 1, overflowed.Handle);
        Assert.Equal((byte)(laneCapacity + 1), overflowed.Frames[0][^1]);
        Assert.Contains(harness.Logger.Events, entry => string.Equals(entry.Name, "tcp.redirect.deferred-overflow", StringComparison.Ordinal));

        for (var handle = 1; handle <= laneCapacity; handle++) harness.Coordinator.FlushPendingRedirectInjections(handle);

        Assert.Equal(laneCapacity, harness.Injector.BatchCalls.Count);
        Assert.Equal(0, harness.Coordinator.Diagnostics.RedirectPendingCount);
    }

    [Fact]
    public async Task NonPumpPacketsTakeTheImmediatePathAndLeaveEveryLaneEmpty()
    {
        var pool = new NdisPacketBufferPool(8);
        await using var harness = new RedirectHarness(pool: pool);
        var listener = await harness.EstablishHostRedirectAsync(53000);

        // The setup worker's concurrent-loser reinjection reconstructs its packet over the retained
        // SYN copy (RunSetupPipelineAsync): a materialized lease and a default NativeFrameHandle.
        // The lanes have no append lock, so such a packet must never append.
        await harness.DispatchForwardDataAsync(53000, 1, pumpOwned: false);
        await harness.DispatchReverseDataAsync(Endpoint.From(s_clientIpv4, listener.Port), Endpoint.From(s_destIpv4, 53000), 2, pumpOwned: false);

        Assert.Equal(2, harness.Injector.SingleCalls.Count);
        Assert.Empty(harness.Injector.BatchCalls);
        Assert.Equal(0, harness.Coordinator.Diagnostics.RedirectPendingCount);
        harness.Coordinator.FlushPendingRedirectInjections(AdapterHandle);
        Assert.Empty(harness.Injector.BatchCalls);
        harness.Coordinator.DebugAssertNoPendingRedirectInjections();
        var stats = pool.Stats;
        Assert.Equal(stats.Rented, stats.Returned);
    }

    [Fact]
    public async Task DebugGuardPassesOnceTheIterationFlushReleasedEveryLane()
    {
        await using var harness = new RedirectHarness();
        await harness.EstablishHostRedirectAsync(53000);
        await harness.DispatchForwardDataAsync(53000, 1);
        await harness.DispatchForwardDataAsync(53000, 2);

        harness.Coordinator.FlushPendingRedirectInjections(AdapterHandle);

        // DEBUG-only assertion (elided in Release, like DebugAssertNoPendingPasses): a surviving lane
        // would mean a missed flush and frames that never leave.
        harness.Coordinator.DebugAssertNoPendingRedirectInjections();
        Assert.Equal(0, harness.Coordinator.Diagnostics.RedirectPendingCount);
        Assert.Single(harness.Injector.BatchCalls);
    }

    [Fact]
    public async Task PumpOwnedFrameIsRewrittenInPlaceWithoutRentalOrMaterialization()
    {
        var pool = new NdisPacketBufferPool(8);
        await using var harness = new RedirectHarness(pool: pool);
        var listener = await harness.EstablishHostRedirectAsync(53000);

        var slot = await harness.DispatchForwardDataInPlaceAsync(53000, 1);
        harness.Coordinator.FlushPendingRedirectInjections(AdapterHandle);

        var rewritten = slot.GetFrame();
        Assert.Equal(new IPAddress([192, 0, 2, 53]), new IPAddress(rewritten[26..30]));
        Assert.Equal(new IPAddress([192, 0, 2, 10]), new IPAddress(rewritten[30..34]));
        Assert.Equal(53000, BinaryPrimitives.ReadUInt16BigEndian(rewritten[34..36]));
        Assert.Equal(listener.Port, BinaryPrimitives.ReadUInt16BigEndian(rewritten[36..38]));
        Assert.Equal((byte)1, rewritten[^1]);
        Assert.Equal(0, pool.Stats.Rented);
        Assert.False(harness.LastLease!.IsMaterialized);
        Assert.Single(harness.Injector.BatchCalls);
    }

    /// <summary>
    /// A packet that never parsed carries <c>default(PacketLayout)</c>, whose zeroed transport reads
    /// as TCP, whose zero IP header length puts the transport header where the IP header starts and
    /// whose zeroed family reads as IPv4. The data leg must refuse that layout rather than apply it —
    /// the frame the leg would have sent stays byte-identical and the association fails closed.
    /// </summary>
    [Fact]
    public async Task RedirectLegRefusesADefaultedLayoutByteIdentically()
    {
        await using var harness = new RedirectHarness();
        await harness.EstablishHostRedirectAsync(53000);
        var pristine = BuildIpv4TcpFrame(s_clientIpv4, s_destIpv4, 53000, 443, payload: [1]);

        var (outcome, frame) = await harness.DispatchForwardDataWithoutLayoutAsync(53000, 1);

        Assert.Equal(TcpRedirectOutcome.Blocked, outcome);
        Assert.Equal(pristine, frame);
        Assert.Equal(0, harness.Coordinator.Diagnostics.RedirectPendingCount);
    }

    [Fact]
    public async Task MaterializedLeaseTakesThePooledFallbackAndReturnsItExactlyOnce()
    {
        var pool = new NdisPacketBufferPool(8);
        await using var harness = new RedirectHarness(pool: pool);
        await harness.EstablishHostRedirectAsync(53000);

        await harness.DispatchForwardDataWithMaterializedLeaseAsync(53000, 1);
        Assert.Equal(1, pool.Stats.Rented);

        harness.Coordinator.FlushPendingRedirectInjections(AdapterHandle);

        var stats = pool.Stats;
        Assert.Equal(stats.Rented, stats.Returned);
        Assert.Equal(0, stats.Outstanding);
        Assert.Single(harness.Injector.BatchCalls);
    }

    /// <summary>
    /// The client port a crafted reset is addressed to (IPv4 TCP destination port at 14 + 20 + 2),
    /// or 0 for a frame that is not a reset (flags byte 14 + 20 + 13 = RST|ACK = 0x14) — which is how
    /// this suite tells one association's reset apart from a listener SYN|ACK on the same wire tuple.
    /// </summary>
    private static ushort ResetDestinationPort(RedirectCall call) =>
        call.Frames[0][47] == 0x14 ? BinaryPrimitives.ReadUInt16BigEndian(call.Frames[0].AsSpan(36, 2)) : (ushort)0;

    private enum RedirectCallKind
    {
        Async,
        Single,
        Batch,
    }

    private sealed record RedirectCall(RedirectCallKind Kind, nint Handle, bool TowardMstcp, byte[][] Frames, bool Failed);

    /// <summary>
    /// A coordinator wired like the runtime composition over the shared fakes: the redirect setup
    /// runs for real (listener allocation, table claim, association), the injector records every
    /// call in order, and the frame pool is dedicated so a rent-site balance regression is exact.
    /// </summary>
    private sealed class RedirectHarness : IAsyncDisposable
    {
        private readonly FakeListenerFactory _listeners = new();
        private readonly List<NdisPacketBuffer> _captureSlots = [];

        public RedirectHarness(
            bool throwOnBatch = false,
            bool failDataFrameSends = false,
            bool faultFailureTailWarn = false,
            IPAddress? forwardLocal = null,
            int? capacity = null,
            NdisPacketBufferPool? pool = null)
        {
            Logger = new RecordingRuntimeLogger();
            Injector = new RecordingRedirectInjector { ThrowOnBatch = throwOnBatch, FailingSingleFrameLength = failDataFrameSends ? DataFrameLength : null };
            Table = new TcpRedirectTable();
            Pool = pool ?? new NdisPacketBufferPool(8);
            Coordinator = CreateCoordinator(
                _listeners,
                new FakeRelayFactory(),
                Injector,
                Table,
                new SelfTrafficRegistry(),
                new FakeLocalAddressProvider(forwardLocal),
                new TcpRedirectOptions
                {
                    Logger = faultFailureTailWarn ? new FaultingFailureTailLogger(Logger) : Logger,
                    Capacity = capacity,
                    FramePool = Pool,
                });
        }

        public TcpProxyCoordinator Coordinator { get; }

        public RecordingRedirectInjector Injector { get; }

        public TcpRedirectTable Table { get; }

        public RecordingRuntimeLogger Logger { get; }

        private NdisPacketBufferPool Pool { get; }

        public async Task<Endpoint> EstablishHostRedirectAsync(ushort clientPort)
        {
            var before = _listeners.Listeners.Count;
            Assert.Equal(TcpRedirectOutcome.SetupPending, await Coordinator.HandleSynAsync(HostSyn(clientPort), s_server, CancellationToken.None));
            await Coordinator.DrainPendingSetupsAsync();
            Assert.Equal(before + 1, _listeners.Listeners.Count);
            return _listeners.Listeners[^1].TranslatedTuple;
        }

        public async Task<Endpoint> EstablishForwardedRedirectAsync(nint adapterHandle = AdapterHandle)
        {
            // Cross-adapter deferral requires the origin adapter to be in the installed scope.
            UpdateRedirectTargets(adapterHandle);
            var before = _listeners.Listeners.Count;
            var syn = MakeForwardedSynPacket(s_clientIpv4, s_destIpv4, 53000, 443) with { Metadata = new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnReceive, adapterHandle) };
            Assert.Equal(TcpRedirectOutcome.SetupPending, await Coordinator.HandleSynAsync(syn, s_server, CancellationToken.None));
            await Coordinator.DrainPendingSetupsAsync();
            Assert.Equal(before + 1, _listeners.Listeners.Count);
            return _listeners.Listeners[^1].TranslatedTuple;
        }

        public void UpdateRedirectTargets(params nint[] activeAdapterHandles) =>
            Coordinator.UpdateRedirectTargets(activeAdapterHandles);

        private static CapturedFlowPacket HostSyn(ushort clientPort) => MakeSynPacket(s_clientIpv4, s_destIpv4, clientPort, 443);

        /// <summary>
        /// Records the listener's SYN|ACK through the reverse hook (the server ISN a crafted client
        /// reset needs) using the non-pump shape, so the control frame stays immediate and no lane
        /// is touched.
        /// </summary>
        public async Task RecordServerSynAckAsync(Endpoint listener, ushort clientPort)
        {
            var source = Endpoint.From(s_clientIpv4, listener.Port);
            var destination = Endpoint.From(s_destIpv4, clientPort);
            var frame = BuildIpv4TcpFrame(source.Address.ToIPAddress(), destination.Address.ToIPAddress(), source.Port, destination.Port, tcpFlags: 0x12);
            Assert.Equal(TcpRedirectOutcome.Injected, await DispatchAsync(frame, FlowKey.Create(source, destination, TransportProtocol.Tcp, FlowOriginKind.Host), AdapterHandle, pumpOwned: false));
        }

        public ValueTask<TcpRedirectOutcome> DispatchForwardDataAsync(ushort clientPort, byte marker, nint adapterHandle = AdapterHandle, bool pumpOwned = true)
        {
            var key = FlowKey.Create(Endpoint.From(s_clientIpv4, clientPort), Endpoint.From(s_destIpv4, 443), TransportProtocol.Tcp, FlowOriginKind.Host);
            return DispatchAsync(BuildIpv4TcpFrame(s_clientIpv4, s_destIpv4, clientPort, 443, payload: [marker]), key, adapterHandle, pumpOwned);
        }

        public ValueTask<TcpRedirectOutcome> DispatchForwardedDataAsync(ushort clientPort, byte marker, nint adapterHandle = AdapterHandle, bool pumpOwned = true)
        {
            var key = FlowKey.Create(Endpoint.From(s_clientIpv4, clientPort), Endpoint.From(s_destIpv4, 443), TransportProtocol.Tcp, FlowOriginKind.Forwarded, FlowBuilders.SlotOf("veth-1", 7), 7);
            return DispatchAsync(BuildIpv4TcpFrame(s_clientIpv4, s_destIpv4, clientPort, 443, payload: [marker]), key, adapterHandle, pumpOwned);
        }

        public ValueTask<TcpRedirectOutcome> DispatchReverseDataAsync(Endpoint source, Endpoint destination, byte marker, bool pumpOwned = true, nint adapterHandle = AdapterHandle)
        {
            var key = FlowKey.Create(source, destination, TransportProtocol.Tcp, FlowOriginKind.Host);
            return DispatchAsync(BuildIpv4TcpFrame(source.Address.ToIPAddress(), destination.Address.ToIPAddress(), source.Port, destination.Port, payload: [marker]), key, adapterHandle, pumpOwned);
        }

        /// <summary>The lease of the most recent pump-owned dispatch, for the materialization assertion.</summary>
        public PacketLease? LastLease { get; private set; }

        /// <summary>The outcome of the most recent pump-owned dispatch.</summary>
        private TcpRedirectOutcome LastOutcome { get; set; }

        public Task<NdisPacketBuffer> DispatchForwardDataInPlaceAsync(ushort clientPort, byte marker) =>
            DispatchKeepingSlotAsync(ForwardDataFrame(clientPort, marker), ForwardKey(clientPort), AdapterHandle, materializeLease: false);

        /// <summary>
        /// Drives the forward data leg with a packet whose layout was never derived from a parse — the
        /// shape a hand-built <see cref="CapturedFlowPacket"/> carries — and reports the outcome with
        /// the capture slot's bytes, which is what this leg would hand the wire.
        /// </summary>
        public async Task<(TcpRedirectOutcome Outcome, byte[] Frame)> DispatchForwardDataWithoutLayoutAsync(ushort clientPort, byte marker)
        {
            var capture = await DispatchKeepingSlotAsync(ForwardDataFrame(clientPort, marker), ForwardKey(clientPort), AdapterHandle, materializeLease: false, withLayout: false).ConfigureAwait(false);
            return (LastOutcome, capture.GetFrame().ToArray());
        }

        public Task<NdisPacketBuffer> DispatchForwardDataWithMaterializedLeaseAsync(ushort clientPort, byte marker) =>
            DispatchKeepingSlotAsync(ForwardDataFrame(clientPort, marker), ForwardKey(clientPort), AdapterHandle, materializeLease: true);

        private static FlowKey ForwardKey(ushort clientPort) =>
            FlowKey.Create(Endpoint.From(s_clientIpv4, clientPort), Endpoint.From(s_destIpv4, 443), TransportProtocol.Tcp, FlowOriginKind.Host);

        private static byte[] ForwardDataFrame(ushort clientPort, byte marker) =>
            BuildIpv4TcpFrame(s_clientIpv4, s_destIpv4, clientPort, 443, payload: [marker]);

        private async Task<NdisPacketBuffer> DispatchKeepingSlotAsync(byte[] frame, FlowKey key, nint adapterHandle, bool materializeLease, bool withLayout = true)
        {
            var context = FlowBuilders.Context(key, "app.exe", adapterId: "eth0");
            var metadata = new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, adapterHandle);
            var capture = new NdisPacketBuffer();
            _captureSlots.Add(capture);
            capture.SetFrame(frame, NdisApiAbi.PacketFlagOnSend, adapterHandle);
            LastLease = materializeLease ? new PacketLease(frame) : new PacketLease(capture);
            var packet = new CapturedFlowPacket(LastLease, context, metadata, NativeFrame: new NativeFrameHandle(capture), Layout: withLayout ? LayoutOf(frame) : default);
            LastOutcome = await Coordinator.HandlePacketAsync(packet, s_server, CancellationToken.None).ConfigureAwait(false);
            return capture;
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            foreach (var slot in _captureSlots) slot.Dispose();
        }

        private async ValueTask<TcpRedirectOutcome> DispatchAsync(byte[] frame, FlowKey key, nint adapterHandle, bool pumpOwned)
        {
            var context = FlowBuilders.Context(key, "app.exe", adapterId: "eth0");
            var metadata = new PacketCaptureMetadata(NdisApiAbi.PacketFlagOnSend, adapterHandle);
            if (!pumpOwned)
            {
                var materialized = new CapturedFlowPacket(new PacketLease(frame), context, metadata, Layout: LayoutOf(frame));
                return await Coordinator.HandlePacketAsync(materialized, s_server, CancellationToken.None).ConfigureAwait(false);
            }
            // A pump batch slot lives for the whole iteration and is released only after the
            // loop-exit flush, so the harness keeps it alive past the explicit flush calls, exactly
            // like the pump does (NdisCapturePump releases its batch buffers after its final
            // batch-completed callback); deferring the slot into a lane keeps the pump as its owner.
            var capture = new NdisPacketBuffer();
            _captureSlots.Add(capture);
            capture.SetFrame(frame, NdisApiAbi.PacketFlagOnSend, adapterHandle);
            var packet = new CapturedFlowPacket(new PacketLease(capture), context, metadata, NativeFrame: new NativeFrameHandle(capture), Layout: LayoutOf(frame));
            return await Coordinator.HandlePacketAsync(packet, s_server, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records every injection call in order (setup async, immediate single, batched) so a test can
    /// assert the batching shape and that control frames preceded the flush. A failed call is
    /// recorded before it throws, so the degraded path's per-frame attempts stay countable; the
    /// batch and data-frame failure seams model the all-or-nothing ABI and a failed retry.
    /// </summary>
    private sealed class RecordingRedirectInjector : ITcpRedirectInjector
    {
        public List<RedirectCall> Calls { get; } = [];

        /// <summary>When true, every batched call fails (the all-or-nothing ABI shape).</summary>
        public bool ThrowOnBatch { get; init; }

        /// <summary>When set, a single send of a frame this long fails — the data frames this suite dispatches, never the shorter crafted control frames.</summary>
        public int? FailingSingleFrameLength { get; init; }

        public List<RedirectCall> SingleCalls => OfKind(RedirectCallKind.Single);

        public List<RedirectCall> BatchCalls => OfKind(RedirectCallKind.Batch);

        public ValueTask InjectAsync(ReadOnlyMemory<byte> rewrittenFrame, bool towardMstcp, nint adapterHandle, CancellationToken cancellationToken)
        {
            Calls.Add(new RedirectCall(RedirectCallKind.Async, adapterHandle, towardMstcp, [rewrittenFrame.ToArray()], Failed: false));
            return ValueTask.CompletedTask;
        }

        public void Inject(NdisPacketBuffer stagedFrame, bool towardMstcp, nint adapterHandle, CancellationToken cancellationToken)
        {
            var frame = stagedFrame.GetFrame().ToArray();
            var failed = FailingSingleFrameLength == frame.Length;
            Calls.Add(new RedirectCall(RedirectCallKind.Single, adapterHandle, towardMstcp, [frame], failed));
            if (failed) throw new IOException("single injection failed");
        }

        public void InjectBatch(NdisPacketBuffer[] frames, int count, bool towardMstcp, nint adapterHandle)
        {
            var snapshot = new byte[count][];
            for (var index = 0; index < count; index++) snapshot[index] = frames[index].GetFrame().ToArray();
            Calls.Add(new RedirectCall(RedirectCallKind.Batch, adapterHandle, towardMstcp, snapshot, Failed: ThrowOnBatch));
            if (ThrowOnBatch) throw new IOException("batched injection failed");
        }

        private List<RedirectCall> OfKind(RedirectCallKind kind)
        {
            var matches = new List<RedirectCall>();
            foreach (var call in Calls)
            {
                if (call.Kind == kind) matches.Add(call);
            }
            return matches;
        }
    }

    /// <summary>
    /// A recording logger whose structured <c>tcp.redirect.deferred-failed</c> event throws after
    /// recording, modelling a fault inside a frame's failure tail: the tail must be contained per
    /// frame. Plain-text lines never throw, so the containment warn stays observable.
    /// </summary>
    private sealed class FaultingFailureTailLogger(RecordingRuntimeLogger inner) : IRuntimeLogger
    {
        public bool IsEnabled(RuntimeLogLevel level) => inner.IsEnabled(level);

        public void Trace(string message) => inner.Trace(message);

        public void Debug(string message) => inner.Debug(message);

        public void Info(string message) => inner.Info(message);

        public void Warn(string message) => inner.Warn(message);

        public void Error(string message) => inner.Error(message);

        public void Event(RuntimeLogLevel level, string eventName, params RuntimeLogField[] fields)
        {
            inner.Event(level, eventName, fields);
            if (string.Equals(eventName, "tcp.redirect.deferred-failed", StringComparison.Ordinal)) throw new InvalidOperationException("failure tail fault");
        }
    }
}
