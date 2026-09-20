using System.Net;
using WinForward.Configuration;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using Xunit;
using static WinForward.Core.Tests.FrameBuilders;

namespace WinForward.Core.Tests;

/// <summary>
/// Batched pass accumulation of <see cref="NdisPacketActionExecutor"/> (task 08-30-batched-ioctls
/// D2): same-(adapter, direction) passes accumulate in capture order and leave in one batched
/// reinjector call at flush; rented pooled buffers return exactly once; in-place capture buffers
/// are never returned; lane overflow degrades to the immediate single send. Since task
/// 09-12-lane-table-scope-sizing the lane table is sized from the capture scope: every
/// <see cref="NdisPacketActionExecutor.RetireLanesExcept"/> call (the scope-installed callback)
/// rebuilds the table at 2 × scope-count lanes, migrating in-scope lanes with their pending
/// frames, so in-scope keys never overflow; overflow remains reachable only against the
/// pre-install table (capacity 8) or the zero-capacity paused table.
/// </summary>
public sealed class NdisPacketActionExecutorBatchingTests
{
    [Fact]
    public async Task SameLaneSendsAccumulatedFramesInAppendOrder()
    {
        var reinjector = new FakeReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);
        using var first = new NdisPacketBuffer();
        using var second = new NdisPacketBuffer();
        using var third = new NdisPacketBuffer();
        first.SetFrame([1], NdisApiAbi.PacketFlagOnReceive, 9);
        second.SetFrame([2], NdisApiAbi.PacketFlagOnReceive, 9);
        third.SetFrame([3], NdisApiAbi.PacketFlagOnReceive, 9);

        await executor.PassAsync(InPlacePass(first));
        await executor.PassAsync(InPlacePass(second));
        await executor.PassAsync(InPlacePass(third));

        Assert.Equal(3, executor.PendingPassCount);
        Assert.Empty(reinjector.BatchCalls);
        Assert.Equal(0, reinjector.ToAdapterCount + reinjector.ToMstcpCount);

        executor.FlushPendingPasses(9);

        var call = Assert.Single(reinjector.BatchCalls);
        Assert.Equal(9, call.AdapterHandle);
        Assert.False(call.ToAdapter);
        Assert.Equal(3, call.Frames.Length);
        Assert.Equal(new byte[] { 1 }, call.Frames[0]);
        Assert.Equal(new byte[] { 2 }, call.Frames[1]);
        Assert.Equal(new byte[] { 3 }, call.Frames[2]);
        Assert.Same(first, call.Buffers[0]);
        Assert.Same(second, call.Buffers[1]);
        Assert.Same(third, call.Buffers[2]);
        Assert.Equal(0, executor.PendingPassCount);
    }

    [Fact]
    public async Task SendAndReceiveLanesFlushAsIndependentBatchedCalls()
    {
        var reinjector = new FakeReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);
        using var toAdapter = new NdisPacketBuffer();
        using var toMstcpA = new NdisPacketBuffer();
        using var toMstcpB = new NdisPacketBuffer();
        toAdapter.SetFrame([0xA0], NdisApiAbi.PacketFlagOnSend, 9);
        toMstcpA.SetFrame([0xB0], NdisApiAbi.PacketFlagOnReceive, 9);
        toMstcpB.SetFrame([0xB1], NdisApiAbi.PacketFlagOnReceive, 9);

        await executor.PassAsync(InPlacePass(toMstcpA, TransportProtocol.Udp));
        await executor.PassAsync(InPlacePass(toAdapter));
        await executor.PassAsync(InPlacePass(toMstcpB, TransportProtocol.Udp));

        executor.FlushPendingPasses(9);

        Assert.Equal(2, reinjector.BatchCalls.Count);
        var mstcpLane = reinjector.BatchCalls.Single(call => !call.ToAdapter);
        Assert.Equal(2, mstcpLane.Frames.Length);
        Assert.Equal(new byte[] { 0xB0 }, mstcpLane.Frames[0]);
        Assert.Equal(new byte[] { 0xB1 }, mstcpLane.Frames[1]);
        var adapterLane = reinjector.BatchCalls.Single(call => call.ToAdapter);
        Assert.Equal(new byte[] { 0xA0 }, adapterLane.Frames[0]);
    }

    [Fact]
    public void FlushWithoutPendingPassesIsANoOp()
    {
        var reinjector = new CountingReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);

        executor.FlushPendingPasses(9);
        executor.FlushPendingPasses(9);

        Assert.Equal(0, reinjector.TotalSendCalls);
        Assert.Equal(0, executor.PendingPassCount);
    }

    [Fact]
    public async Task MaterializedAndInPlaceFramesKeepAppendOrderAndReturnRentedBuffersExactlyOnce()
    {
        var reinjector = new FakeReinjector();
        var pool = new NdisPacketBufferPool(4);
        var executor = new NdisPacketActionExecutor(reinjector, bufferPool: pool);
        using var inPlaceA = new NdisPacketBuffer();
        using var inPlaceB = new NdisPacketBuffer();
        inPlaceA.SetFrame([0x10], NdisApiAbi.PacketFlagOnSend, 9);
        inPlaceB.SetFrame([0x12], NdisApiAbi.PacketFlagOnSend, 9);

        // Interleaved materialized (pooled copy) and in-place (capture buffer) passes on one lane:
        // the flush must preserve append order and return only the rented buffers.
        await executor.PassAsync(InPlacePass(inPlaceA));
        await executor.PassAsync(MaterializedPass([0x11]));
        await executor.PassAsync(InPlacePass(inPlaceB));
        Assert.Equal(0, pool.Count); // the pooled copy is rented and pending, not idle in the pool

        executor.FlushPendingPasses(9);
        executor.FlushPendingPasses(9); // a second flush must not double-return anything.

        var call = Assert.Single(reinjector.BatchCalls);
        Assert.Equal(3, call.Frames.Length);
        Assert.Equal(new byte[] { 0x10 }, call.Frames[0]);
        Assert.Equal(new byte[] { 0x11 }, call.Frames[1]);
        Assert.Equal(new byte[] { 0x12 }, call.Frames[2]);
        Assert.Same(inPlaceA, call.Buffers[0]);
        Assert.Same(inPlaceB, call.Buffers[2]);
        Assert.Equal(1, pool.Count); // exactly the one rented buffer came back, exactly once.

        // The in-place capture buffers stay alive and untouched by the flush (the pump owns them).
        Assert.Equal(new byte[] { 0x10 }, inPlaceA.GetFrame().ToArray());
        Assert.Equal(new byte[] { 0x12 }, inPlaceB.GetFrame().ToArray());
        executor.DebugAssertNoPendingPasses(9);
    }

    [Fact]
    public async Task LaneOverflowDegradesToImmediateSingleSend()
    {
        var reinjector = new CountingReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);

        // Pre-install lane table (no scope installed yet): 5 adapters x 2 directions = 10 lanes
        // exceed the capacity of 8, so the last lane pair must fall back to the immediate single
        // send instead of being silently batched.
        for (var adapter = 1; adapter <= 5; adapter++)
        {
            await executor.PassAsync(MaterializedPass([0x21], adapter, isOnSend: true));
            await executor.PassAsync(MaterializedPass([0x22], adapter, isOnSend: false));
        }

        Assert.Equal(2, reinjector.SendToAdapterCount + reinjector.SendToMstcpCount);
        for (var adapter = 1; adapter <= 5; adapter++) executor.FlushPendingPasses(adapter);

        Assert.Equal(8, reinjector.BatchSendToAdapterCount + reinjector.BatchSendToMstcpCount);
        Assert.Equal(8, reinjector.BatchedPacketsToAdapter + reinjector.BatchedPacketsToMstcp);
    }

    [Fact]
    public async Task FailingBatchStillReturnsRentedBuffers()
    {
        var reinjector = new ThrowingBatchReinjector();
        var pool = new NdisPacketBufferPool(4);
        var executor = new NdisPacketActionExecutor(reinjector, bufferPool: pool);

        await executor.PassAsync(MaterializedPass([0x30]));
        Assert.Equal(0, pool.Count);

        Assert.Throws<InvalidOperationException>(() => executor.FlushPendingPasses(9));

        // The exactly-once release contract survives a failed batch: the rented buffer returned
        // in the flush's finally even though the reinjector threw.
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public async Task LaneOverflowSendFailureStillReturnsTheRentedBuffer()
    {
        // L1 regression: the overflow path's immediate single send can throw, and the rented
        // pooled copy must still return exactly once — the release used to sit after the
        // catch-rethrow, where a throw made it unreachable and leaked the native buffer.
        var reinjector = new ThrowingSingleReinjector();
        var logger = new RecordingRuntimeLogger();
        var pool = new NdisPacketBufferPool(4);
        var executor = new NdisPacketActionExecutor(reinjector, logger, bufferPool: pool);

        // Fill the pre-install lane table (8 lanes) with four adapters × two directions so the
        // next distinct key degrades to the immediate single-send overflow path.
        for (var adapter = 1; adapter <= 4; adapter++)
        {
            await executor.PassAsync(MaterializedPass([0x24], adapter, isOnSend: true));
            await executor.PassAsync(MaterializedPass([0x25], adapter, isOnSend: false));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.PassAsync(MaterializedPass([0x26], 5, isOnSend: true)).AsTask());

        // Nine materialized passes rented nine pooled copies: eight sit pending in their lanes,
        // and the overflow copy that failed to send came back despite the throw. The failure
        // keeps its logging semantics — counted, warned once, then rethrown.
        var stats = pool.Stats;
        Assert.Equal(9, stats.Rented);
        Assert.Equal(1, stats.Returned);
        Assert.Equal(1, pool.Count);
        Assert.Equal(1, logger.Events.Count(@event => string.Equals(@event.Name, "reinject.pass-failed", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ImmediateFlushMatchesSingleSendBehavior()
    {
        // Compatibility shape: a pass followed by an immediate flush behaves exactly like the
        // pre-batching single send — same frame bytes, adapter handle, direction, and NDIS flags.
        var reinjector = new FakeReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);
        using var buffer = new NdisPacketBuffer();
        var frame = CreateIpv4UdpFrame();
        buffer.SetFrame(frame, NdisApiAbi.PacketFlagOnSend, 0x77, flags: 0x33);

        await executor.PassAsync(InPlacePass(buffer, TransportProtocol.Udp));
        executor.FlushPendingPasses(0x77);

        var call = Assert.Single(reinjector.BatchCalls);
        Assert.Equal(0x77, call.AdapterHandle);
        Assert.True(call.ToAdapter);
        Assert.Equal(frame, call.Frames[0]);
        Assert.Equal(0x33u, reinjector.LastFlags);
        Assert.Equal(0, executor.PendingPassCount);
    }

    [Fact]
    public void PassAsyncWithoutLeaseThrowsArgumentNullExceptionNamingTheLease()
    {
        // The packet is a struct and can never be null itself; when the lease is the null part,
        // the guard's ParamName must point at packet.Lease so stack traces name the real problem.
        var executor = new NdisPacketActionExecutor(new CountingReinjector());
        var packet = new CapturedFlowPacket(
            null!,
            FlowContext(FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 1), Endpoint.From(IPAddress.Parse("192.0.2.53"), 2), TransportProtocol.Tcp, FlowOriginKind.Host)));

#pragma warning disable CA2012 // The lease guard throws synchronously before any ValueTask is produced; the Action-bound lambda pins exactly that synchronous exception and nothing consumes a result.
        var exception = Assert.Throws<ArgumentNullException>(() => executor.PassAsync(packet));
#pragma warning restore CA2012

        Assert.Equal("packet.Lease", exception.ParamName);
    }

    [Fact]
    public async Task LaneOverflowIsCountedAndWarnedOncePerWindow()
    {
        var reinjector = new FakeReinjector();
        var logger = new RecordingRuntimeLogger();
        var executor = new NdisPacketActionExecutor(reinjector, logger);

        // Fill the pre-install lane capacity (8) with four adapters × two directions; no scope
        // has been installed, so the table still holds its compile-time pre-install size.
        for (var adapter = 1; adapter <= 4; adapter++)
        {
            await executor.PassAsync(MaterializedPass([0x40], adapter, isOnSend: true));
            await executor.PassAsync(MaterializedPass([0x41], adapter, isOnSend: false));
        }

        // The 9th distinct (adapter, direction) key degrades to the immediate single send —
        // observed, never silent: the overflow counter ticks and the warn fires.
        await executor.PassAsync(MaterializedPass([0x42], 5, isOnSend: true));
        Assert.Equal(1L, executor.ImmediateSendLaneOverflowCount);
        Assert.Equal(1, reinjector.ToAdapterCount + reinjector.ToMstcpCount);
        Assert.Equal("B"u8.ToArray(), reinjector.LastFrame);

        // A second overflow inside the rate-limit window still counts and still sends exactly
        // once, but the warn fires at most once per window.
        await executor.PassAsync(MaterializedPass([0x43], 5, isOnSend: false));
        Assert.Equal(2L, executor.ImmediateSendLaneOverflowCount);
        Assert.Equal(2, reinjector.ToAdapterCount + reinjector.ToMstcpCount);
        Assert.Equal(1, logger.Lines.Count(line => line.Level == RuntimeLogLevel.Warn && line.Message.Contains("degraded", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RetireLanesExceptFreesSlotsForFreshHandles()
    {
        var reinjector = new FakeReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);

        // One generation's whole lane table (handles 1..4, two directions each), flushed as the
        // pump's iteration-end callback would before the generation stops.
        for (var adapter = 1; adapter <= 4; adapter++)
        {
            await executor.PassAsync(MaterializedPass([0x50], adapter, isOnSend: true));
            await executor.PassAsync(MaterializedPass([0x51], adapter, isOnSend: false));
        }
        for (var adapter = 1; adapter <= 4; adapter++) executor.FlushPendingPasses(adapter);

        // Generation switch on fresh handles: the scope install rebuilds the table for the new
        // scope (handles 101, 102 — the old generation's lanes are retired, not migrated), so
        // the next generation's keys accumulate instead of degrading to immediate sends.
        executor.RetireLanesExcept([101, 102]);

        await executor.PassAsync(MaterializedPass([0x52], 101, isOnSend: true));
        await executor.PassAsync(MaterializedPass([0x53], 101, isOnSend: false));
        await executor.PassAsync(MaterializedPass([0x54], 102, isOnSend: true));
        await executor.PassAsync(MaterializedPass([0x55], 102, isOnSend: false));

        Assert.Equal(0L, executor.ImmediateSendLaneOverflowCount);
        Assert.Equal(0, reinjector.ToAdapterCount + reinjector.ToMstcpCount);
        Assert.Equal(4, executor.PendingPassCount);
    }

    [Fact]
    public async Task RetireLanesExceptWithEmptySpanRetiresEveryLane()
    {
        var reinjector = new FakeReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);

        for (var adapter = 1; adapter <= 4; adapter++)
        {
            await executor.PassAsync(MaterializedPass([0x60], adapter, isOnSend: true));
            await executor.PassAsync(MaterializedPass([0x61], adapter, isOnSend: false));
        }
        for (var adapter = 1; adapter <= 4; adapter++) executor.FlushPendingPasses(adapter);

        // Empty scope = interception paused: the rebuild installs a zero-capacity table (no
        // lane can accumulate while no pump runs), so a stray pass cannot batch — it degrades
        // to the immediate single send, still going out exactly once.
        executor.RetireLanesExcept([]);

        await executor.PassAsync(MaterializedPass([0x62], 11, isOnSend: true));
        await executor.PassAsync(MaterializedPass([0x63], 11, isOnSend: false));
        Assert.Equal(2L, executor.ImmediateSendLaneOverflowCount);
        Assert.Equal(2, reinjector.ToAdapterCount + reinjector.ToMstcpCount);
        Assert.Equal(0, executor.PendingPassCount);

        // The next scope install rebuilds capacity: batching resumes on the fresh keys with
        // zero further overflow growth.
        executor.RetireLanesExcept([11, 12, 13, 14]);

        for (var adapter = 11; adapter <= 14; adapter++)
        {
            await executor.PassAsync(MaterializedPass([0x64], adapter, isOnSend: true));
            await executor.PassAsync(MaterializedPass([0x65], adapter, isOnSend: false));
        }

        Assert.Equal(2L, executor.ImmediateSendLaneOverflowCount);
        Assert.Equal(2, reinjector.ToAdapterCount + reinjector.ToMstcpCount);
        Assert.Equal(8, executor.PendingPassCount);
    }

    [Fact]
    public async Task ScopeInstallRebuildsLaneCapacityBeyondThePreInstallTableAndMigratesPendingLanes()
    {
        var reinjector = new FakeReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);

        // Pre-install table: 5 adapters × 2 directions = 10 lanes against a capacity of 8, so
        // the fifth adapter's lane pair overflows to immediate single sends. The other eight
        // lanes keep their pending frames (no iteration-end flush ran).
        for (var adapter = 1; adapter <= 5; adapter++)
        {
            await executor.PassAsync(MaterializedPass([0x80], adapter, isOnSend: true));
            await executor.PassAsync(MaterializedPass([0x81], adapter, isOnSend: false));
        }
        Assert.Equal(2L, executor.ImmediateSendLaneOverflowCount);
        Assert.Equal(2, reinjector.ToAdapterCount + reinjector.ToMstcpCount);
        Assert.Equal(8, executor.PendingPassCount);

        // Scope install with all five adapters: the table is rebuilt at 2 × 5 = 10 lanes and
        // every in-scope lane migrates with its pending frames — nothing is stranded.
        executor.RetireLanesExcept([1, 2, 3, 4, 5]);
        Assert.Equal(8, executor.PendingPassCount);

        // The previously-overflowing adapter's both directions now accumulate in lanes: with a
        // scope-sized table, in-scope keys can never overflow.
        await executor.PassAsync(MaterializedPass([0x82], 5, isOnSend: true));
        await executor.PassAsync(MaterializedPass([0x83], 5, isOnSend: false));
        Assert.Equal(2L, executor.ImmediateSendLaneOverflowCount);
        Assert.Equal(2, reinjector.ToAdapterCount + reinjector.ToMstcpCount);
        Assert.Equal(10, executor.PendingPassCount);

        // Migration kept the frames deliverable: every lane flushes exactly its accumulated
        // frames in one batched call — the eight pre-rebuild frames and the two fresh ones.
        for (var adapter = 1; adapter <= 5; adapter++) executor.FlushPendingPasses(adapter);
        Assert.Equal(10, reinjector.BatchCalls.Count);
        Assert.Equal(10, reinjector.BatchCalls.Sum(call => call.Frames.Length));
        Assert.Equal(0, executor.PendingPassCount);
    }

    [Fact]
    public async Task RetiringALaneWithPendingFramesWarnsAndReturnsRentedBuffersExactlyOnce()
    {
        var reinjector = new FakeReinjector();
        var logger = new RecordingRuntimeLogger();
        var pool = new NdisPacketBufferPool(4);
        var executor = new NdisPacketActionExecutor(reinjector, logger, bufferPool: pool);
        using var inPlace = new NdisPacketBuffer();
        inPlace.SetFrame([0x70], NdisApiAbi.PacketFlagOnReceive, 7);

        // Breach the between-generations contract on purpose: retire a lane that still holds one
        // rented pooled copy and one in-place capture buffer (no iteration-end flush ran).
        await executor.PassAsync(MaterializedPass([0x71], 7, isOnSend: false));
        await executor.PassAsync(InPlacePass(inPlace));
        Assert.Equal(0, pool.Count);

        executor.RetireLanesExcept([8]);

        // The frames drop fail-closed (their adapter is gone — no send of any kind), the rented
        // buffer returns exactly once, the in-place buffer stays pump-owned, and the breach
        // surfaces as a warn.
        Assert.Equal(0, reinjector.ToAdapterCount + reinjector.ToMstcpCount);
        Assert.Empty(reinjector.BatchCalls);
        Assert.Equal(1, pool.Count);
        Assert.Equal(0, executor.PendingPassCount);
        Assert.Equal("p"u8.ToArray(), inPlace.GetFrame().ToArray());
        Assert.Contains(logger.Lines, line => line.Level == RuntimeLogLevel.Warn && line.Message.Contains("pass lane retired", StringComparison.Ordinal));

        // A repeated retire finds no lane and must not double-return anything.
        executor.RetireLanesExcept([8]);
        Assert.Equal(1, pool.Count);
    }

    private static CapturedFlowPacket InPlacePass(NdisPacketBuffer buffer, TransportProtocol protocol = TransportProtocol.Tcp)
    {
        var lease = new PacketLease(buffer);
        return new CapturedFlowPacket(
            lease,
            FlowContext(FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 1), Endpoint.From(IPAddress.Parse("192.0.2.53"), 2), protocol, FlowOriginKind.Host)),
            new PacketCaptureMetadata(buffer.DeviceFlags, buffer.CapturedAdapterHandle, buffer.Flags),
            NativeFrame: new NativeFrameHandle(buffer));
    }

    private static CapturedFlowPacket MaterializedPass(byte[] frame, nint adapterHandle = 9, bool isOnSend = true)
    {
        var lease = new PacketLease(frame);
        _ = lease.Frame.Length; // materialize, as a rewriting consumer would
        return new CapturedFlowPacket(
            lease,
            FlowContext(FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 1), Endpoint.From(IPAddress.Parse("192.0.2.53"), 2), TransportProtocol.Tcp, FlowOriginKind.Host)),
            new PacketCaptureMetadata(isOnSend ? NdisApiAbi.PacketFlagOnSend : NdisApiAbi.PacketFlagOnReceive, adapterHandle));
    }

    private static FlowContext FlowContext(FlowKey key) => new(key, ProcessName: null, ProcessPath: null, AdapterId: null, AdapterName: null, key.Remote.Port);

    private sealed class ThrowingBatchReinjector : IPacketReinjector
    {
        public void SendToAdapter(nint adapterHandle, NdisPacketBuffer buffer) { }

        public void SendToMstcp(nint adapterHandle, NdisPacketBuffer buffer) { }

        public void SendPacketsToAdapter(nint adapterHandle, NdisPacketBuffer[] buffers, int count) => throw new InvalidOperationException("batched injection failed");

        public void SendPacketsToMstcp(nint adapterHandle, NdisPacketBuffer[] buffers, int count) => throw new InvalidOperationException("batched injection failed");
    }

    private sealed class ThrowingSingleReinjector : IPacketReinjector
    {
        public void SendToAdapter(nint adapterHandle, NdisPacketBuffer buffer) => throw new InvalidOperationException("single injection failed");

        public void SendToMstcp(nint adapterHandle, NdisPacketBuffer buffer) => throw new InvalidOperationException("single injection failed");

        public void SendPacketsToAdapter(nint adapterHandle, NdisPacketBuffer[] buffers, int count) { }

        public void SendPacketsToMstcp(nint adapterHandle, NdisPacketBuffer[] buffers, int count) { }
    }
}
