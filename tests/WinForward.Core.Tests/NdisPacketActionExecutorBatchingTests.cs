using System.Net;
using WinForward.Configuration;
using WinForward.Core;
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
/// are never returned; lane overflow degrades to the immediate single send.
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
        first.SetFrame([1], NdisApiAbi.PacketFlagOnReceive, (nint)9);
        second.SetFrame([2], NdisApiAbi.PacketFlagOnReceive, (nint)9);
        third.SetFrame([3], NdisApiAbi.PacketFlagOnReceive, (nint)9);

        await executor.PassAsync(InPlacePass(first), CancellationToken.None);
        await executor.PassAsync(InPlacePass(second), CancellationToken.None);
        await executor.PassAsync(InPlacePass(third), CancellationToken.None);

        Assert.Equal(3, executor.PendingPassCount);
        Assert.Empty(reinjector.BatchCalls);
        Assert.Equal(0, reinjector.ToAdapterCount + reinjector.ToMstcpCount);

        executor.FlushPendingPasses((nint)9);

        var call = Assert.Single(reinjector.BatchCalls);
        Assert.Equal((nint)9, call.AdapterHandle);
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
        toAdapter.SetFrame([0xA0], NdisApiAbi.PacketFlagOnSend, (nint)9);
        toMstcpA.SetFrame([0xB0], NdisApiAbi.PacketFlagOnReceive, (nint)9);
        toMstcpB.SetFrame([0xB1], NdisApiAbi.PacketFlagOnReceive, (nint)9);

        await executor.PassAsync(InPlacePass(toMstcpA, TransportProtocol.Udp), CancellationToken.None);
        await executor.PassAsync(InPlacePass(toAdapter, TransportProtocol.Tcp), CancellationToken.None);
        await executor.PassAsync(InPlacePass(toMstcpB, TransportProtocol.Udp), CancellationToken.None);

        executor.FlushPendingPasses((nint)9);

        Assert.Equal(2, reinjector.BatchCalls.Count);
        var mstcpLane = reinjector.BatchCalls.Single(call => !call.ToAdapter);
        Assert.Equal(2, mstcpLane.Frames.Length);
        Assert.Equal(new byte[] { 0xB0 }, mstcpLane.Frames[0]);
        Assert.Equal(new byte[] { 0xB1 }, mstcpLane.Frames[1]);
        var adapterLane = reinjector.BatchCalls.Single(call => call.ToAdapter);
        Assert.Equal(new byte[] { 0xA0 }, adapterLane.Frames[0]);
    }

    [Fact]
    public async Task FlushWithoutPendingPassesIsANoOp()
    {
        var reinjector = new CountingReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);

        executor.FlushPendingPasses((nint)9);
        executor.FlushPendingPasses((nint)9);

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
        inPlaceA.SetFrame([0x10], NdisApiAbi.PacketFlagOnSend, (nint)9);
        inPlaceB.SetFrame([0x12], NdisApiAbi.PacketFlagOnSend, (nint)9);

        // Interleaved materialized (pooled copy) and in-place (capture buffer) passes on one lane:
        // the flush must preserve append order and return only the rented buffers.
        await executor.PassAsync(InPlacePass(inPlaceA), CancellationToken.None);
        await executor.PassAsync(MaterializedPass([0x11]), CancellationToken.None);
        await executor.PassAsync(InPlacePass(inPlaceB), CancellationToken.None);
        Assert.Equal(0, pool.Count); // the pooled copy is rented and pending, not idle in the pool

        executor.FlushPendingPasses((nint)9);
        executor.FlushPendingPasses((nint)9); // a second flush must not double-return anything.

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
        executor.DebugAssertNoPendingPasses((nint)9);
    }

    [Fact]
    public async Task LaneOverflowDegradesToImmediateSingleSend()
    {
        var reinjector = new CountingReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);

        // 5 adapters x 2 directions = 10 lanes; the fixed capacity is 8, so the last lane pair
        // must fall back to the immediate single send instead of being silently batched.
        for (var adapter = 1; adapter <= 5; adapter++)
        {
            await executor.PassAsync(MaterializedPass([0x21], (nint)adapter, isOnSend: true), CancellationToken.None);
            await executor.PassAsync(MaterializedPass([0x22], (nint)adapter, isOnSend: false), CancellationToken.None);
        }

        Assert.Equal(2, reinjector.SendToAdapterCount + reinjector.SendToMstcpCount);
        for (var adapter = 1; adapter <= 5; adapter++) executor.FlushPendingPasses((nint)adapter);

        Assert.Equal(8, reinjector.BatchSendToAdapterCount + reinjector.BatchSendToMstcpCount);
        Assert.Equal(8, reinjector.BatchedPacketsToAdapter + reinjector.BatchedPacketsToMstcp);
    }

    [Fact]
    public async Task FailingBatchStillReturnsRentedBuffers()
    {
        var reinjector = new ThrowingBatchReinjector();
        var pool = new NdisPacketBufferPool(4);
        var executor = new NdisPacketActionExecutor(reinjector, bufferPool: pool);

        await executor.PassAsync(MaterializedPass([0x30]), CancellationToken.None);
        Assert.Equal(0, pool.Count);

        Assert.Throws<InvalidOperationException>(() => executor.FlushPendingPasses((nint)9));

        // The exactly-once release contract survives a failed batch: the rented buffer returned
        // in the flush's finally even though the reinjector threw.
        Assert.Equal(1, pool.Count);
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
        buffer.SetFrame(frame, NdisApiAbi.PacketFlagOnSend, (nint)0x77, flags: 0x33);

        await executor.PassAsync(InPlacePass(buffer, TransportProtocol.Udp), CancellationToken.None);
        executor.FlushPendingPasses((nint)0x77);

        var call = Assert.Single(reinjector.BatchCalls);
        Assert.Equal((nint)0x77, call.AdapterHandle);
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

        var exception = Assert.Throws<ArgumentNullException>(() => executor.PassAsync(packet, CancellationToken.None));

        Assert.Equal("packet.Lease", exception.ParamName);
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

    private static FlowContext FlowContext(FlowKey key) => new(key, null, null, null, null, key.Remote.Port);

    private sealed class ThrowingBatchReinjector : IPacketReinjector
    {
        public void SendToAdapter(nint adapterHandle, NdisPacketBuffer buffer) { }

        public void SendToMstcp(nint adapterHandle, NdisPacketBuffer buffer) { }

        public void SendPacketsToAdapter(nint adapterHandle, NdisPacketBuffer[] buffers, int count) => throw new InvalidOperationException("batched injection failed");

        public void SendPacketsToMstcp(nint adapterHandle, NdisPacketBuffer[] buffers, int count) => throw new InvalidOperationException("batched injection failed");
    }
}
