using System.Net;
using System.Runtime.Versioning;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Performance.Tests;

/// <summary>
/// Exact zero-allocation gates for the kernel pass mirror's hot path: one pass packet through the
/// executor with the observer wired, and one first observation of a tuple through the cache's marker
/// and ring. Both windows are opened after a warm-up on the same thread and are paired with a
/// call-count assertion, so a window that silently stopped driving the path fails instead of passing.
/// </summary>
public sealed class KernelPassObservationGateTests
{
    private const nint AdapterHandle = 9;

    [Fact]
    public async Task PolicyPassWithAnObserverAllocatesNoManagedBytes()
    {
        var observer = new CountingPassObserver();
        var reinjector = new CountingReinjector();
        var executor = new NdisPacketActionExecutor(reinjector, passFlowObserver: observer);
        using var buffer = new NdisPacketBuffer();
        buffer.SetFrame(FrameBuilders.CreateIPv4UdpFrame(), NdisApiAbi.PacketFlagOnSend, AdapterHandle);
        var key = FlowKey.Create(
            Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000),
            Endpoint.From(IPAddress.Parse("192.0.2.53"), 443),
            TransportProtocol.Tcp,
            FlowOriginKind.Host);
        var packet = new CapturedFlowPacket(
            new PacketLease(buffer),
            FlowBuilders.Context(key),
            new PacketCaptureMetadata(buffer.DeviceFlags, buffer.CapturedAdapterHandle, buffer.Flags),
            NativeFrame: new NativeFrameHandle(buffer),
            PassKind: PacketPassKind.Flow);

        for (var warm = 0; warm < 8; warm++)
        {
            await executor.PassAsync(packet);
            executor.FlushPendingPasses(AdapterHandle);
        }

        var threadId = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        const int count = 64;
        for (var index = 0; index < count; index++)
        {
            var pending = executor.PassAsync(packet);
            Assert.True(pending.IsCompletedSuccessfully, "the gate relies on the pass path completing synchronously");
            await pending;
            executor.FlushPendingPasses(AdapterHandle);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(threadId, Environment.CurrentManagedThreadId);
        Assert.Equal(0, allocated);
        Assert.Equal(8 + count, observer.Calls);
        Assert.Equal(8 + count, reinjector.BatchSendToAdapterCount);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task KernelPassCacheObservationAllocatesNoManagedBytes()
    {
        var table = new RecordingKernelFilterTable();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero));
        var cache = new NdisKernelPassCache(table, timeProvider: clock, counters: new RuntimeCounters());
        try
        {
            cache.Start();
            var keys = CreateKeys(count: 256);

            var threadId = Environment.CurrentManagedThreadId;
            var before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var key in keys) cache.OnPassPacket(in key);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(threadId, Environment.CurrentManagedThreadId);
            Assert.Equal(0, allocated);
            // The window really drove the path: every distinct tuple claimed a marker and queued a
            // candidate, and no driver operation happened on this thread.
            Assert.Equal(keys.Length, cache.PendingCandidates);
            Assert.Equal(["reset"], table.Operations);
        }
        finally
        {
            await cache.DisposeAsync();
        }
    }

    private static FlowKey[] CreateKeys(int count)
    {
        var keys = new FlowKey[count];
        for (var index = 0; index < count; index++)
        {
            keys[index] = FlowKey.Create(
                Endpoint.From(IPAddress.Parse("192.0.2.10"), (ushort)(40_000 + index)),
                Endpoint.From(IPAddress.Parse("198.51.100.7"), 443),
                TransportProtocol.Tcp,
                FlowOriginKind.Host);
        }

        return keys;
    }

    private sealed class CountingPassObserver : IKernelPassFlowObserver
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void OnPassPacket(in FlowKey key) => _ = Interlocked.Increment(ref _calls);
    }
}
