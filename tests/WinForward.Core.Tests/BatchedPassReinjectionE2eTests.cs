using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Windows;
using Xunit;
using static WinForward.Core.Tests.FrameBuilders;

namespace WinForward.Core.Tests;

/// <summary>
/// End-to-end batched pass reinjection (task 08-30-batched-ioctls S5/D3): a capture pump wired
/// like the runtime composition (pump → processor → dispatcher → executor, with the executor
/// flush as the pump's batch-completed callback) must send each iteration's passes as one batched
/// reinjector call per direction — the ≥10× call-reduction acceptance under mixed pass load —
/// while per-direction frames keep capture order, and the loop-exit flush must never strand
/// accumulated frames.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BatchedPassReinjectionE2eTests
{
    private const int BatchCapacity = 32;
    private const int Batches = 3;

    [Fact]
    public async Task PumpFlushesEachIterationIntoOneBatchedSendPerDirection()
    {
        var reinjector = new FakeReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);
        var configuration = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass));
        var dispatcher = new FlowDispatcher(configuration, new FakeGuard(), executor);
        var processor = new CapturePacketProcessor(dispatcher, onBatchCompleted: executor.FlushPendingPasses);
        var adapter = new WindowsAdapter("id-a", "Ethernet", "internal-a", (nint)0x55, 1);
        using var cts = new CancellationTokenSource();
        var reader = new FiniteMixedPassReader(Batches * BatchCapacity, cts, (nint)0x55);

        await using var pump = new NdisCapturePump(
            reader,
            adapter.RuntimeHandle,
            (packet, cancellationToken) => processor.ProcessAsync(packet, adapter, cancellationToken),
            new NdisCapturePumpOptions
            {
                PollDelay = TimeSpan.FromMilliseconds(1),
                BatchCapacity = BatchCapacity,
                OnBatchCompleted = () => executor.FlushPendingPasses(adapter.RuntimeHandle),
            });
        await RunPumpAsync(pump, cts);

        // Every frame went out exactly once, through the batched path, and nothing stayed pending.
        var totalFrames = reinjector.BatchCalls.Sum(call => call.Frames.Length);
        Assert.Equal(Batches * BatchCapacity, totalFrames);
        Assert.Equal(0, reinjector.ToAdapterCount + reinjector.ToMstcpCount);
        Assert.Equal(0, executor.PendingPassCount);

        // Mixed send/receive load: one batched call per direction per iteration => 2 * Batches
        // calls for 96 packets — a 16x reduction versus the per-packet path (>= 10x acceptance).
        Assert.Equal(2 * Batches, reinjector.BatchToAdapterCount + reinjector.BatchToMstcpCount);
        Assert.Equal(Batches * BatchCapacity / 2, reinjector.BatchCalls.Where(call => call.ToAdapter).Sum(call => call.Frames.Length));
        Assert.Equal(Batches * BatchCapacity / 2, reinjector.BatchCalls.Where(call => !call.ToAdapter).Sum(call => call.Frames.Length));

        // Per-direction capture order: the marker byte of each lane's frames is strictly
        // increasing, and every batched call targeted this adapter's enumeration handle.
        var adapterMarkers = reinjector.BatchCalls.Where(call => call.ToAdapter).SelectMany(call => call.Frames).Select(frame => frame[^1]).ToArray();
        var mstcpMarkers = reinjector.BatchCalls.Where(call => !call.ToAdapter).SelectMany(call => call.Frames).Select(frame => frame[^1]).ToArray();
        Assert.Equal(Batches * BatchCapacity / 2, adapterMarkers.Length);
        Assert.Equal(Batches * BatchCapacity / 2, mstcpMarkers.Length);
        Assert.True(IsStrictlyIncreasing(adapterMarkers), "Adapter-direction frames left in a non-capture order.");
        Assert.True(IsStrictlyIncreasing(mstcpMarkers), "MSTCP-direction frames left in a non-capture order.");
        Assert.All(reinjector.BatchCalls, call => Assert.Equal((nint)0x55, call.AdapterHandle));
    }

    [Fact]
    public async Task PumpFlushesPendingPassesOnLoopExit()
    {
        // The handler faults on the slot after some passes have accumulated; the exit flush must
        // still send those frames (nothing is stranded by teardown) before the exception surfaces.
        var reinjector = new FakeReinjector();
        var executor = new NdisPacketActionExecutor(reinjector);
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedFaultingReader((nint)0x66);

        await using var pump = new NdisCapturePump(reader, (nint)0x66, (packet, _) =>
        {
            if (packet.Buffer.GetFrame()[^1] == 0xFF) throw new InvalidOperationException("handler fault");
            var lease = new PacketLease(packet.Buffer);
            var packet2 = new CapturedFlowPacket(
                lease,
                new FlowContext(FlowKeyFor(packet), null, null, null, null, 443),
                new PacketCaptureMetadata(packet.DeviceFlags, packet.AdapterHandle, packet.Flags),
                NativeFrame: new NativeFrameHandle(packet.Buffer));
            return executor.PassAsync(packet2, CancellationToken.None);
        }, new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1), OnBatchCompleted = () => executor.FlushPendingPasses((nint)0x66) });

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunPumpAsync(pump, cts).AsTask());

        var frames = Assert.Single(reinjector.BatchCalls).Frames;
        Assert.Equal(2, frames.Length);
        Assert.Equal((byte)1, frames[0][^1]);
        Assert.Equal((byte)2, frames[1][^1]);
        Assert.Equal(1, reinjector.BatchToAdapterCount);
        Assert.Equal(0, executor.PendingPassCount);
    }

    private static FlowKey FlowKeyFor(NdisCapturedPacket packet) => packet.Buffer.GetFrame()[^1] % 2 == 0
        ? FlowKey.Create(Endpoint.From(System.Net.IPAddress.Parse("192.0.2.10"), 1), Endpoint.From(System.Net.IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Tcp, FlowOriginKind.Host)
        : FlowKey.Create(Endpoint.From(System.Net.IPAddress.Parse("192.0.2.11"), 2), Endpoint.From(System.Net.IPAddress.Parse("192.0.2.53"), 443), TransportProtocol.Tcp, FlowOriginKind.Host);

    private static async ValueTask RunPumpAsync(NdisCapturePump pump, CancellationTokenSource cts)
    {
        try
        {
            await pump.RunAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Reader exhaustion cancels the token: the pump's normal termination path.
        }
    }

    private static bool IsStrictlyIncreasing(byte[] values)
    {
        for (var index = 1; index < values.Length; index++)
        {
            if (values[index] <= values[index - 1]) return false;
        }
        return true;
    }

    /// <summary>
    /// Supplies full batches of alternating-direction pass frames with a strictly increasing
    /// marker byte in the last frame byte, then cancels (the finite-reader termination pattern of
    /// CapturePumpBenchmarks).
    /// </summary>
    private sealed class FiniteMixedPassReader(int totalPackets, CancellationTokenSource completion, nint adapterHandle) : INdisPacketReader
    {
        private readonly byte[] _frame = CreateIpv4TcpFrame();
        private int _sequence;

        public int TryReadPackets(nint handle, NdisPacketBuffer[] buffers)
        {
            var remaining = totalPackets - _sequence;
            if (remaining <= 0)
            {
                completion.Cancel();
                return 0;
            }

            var count = Math.Min(remaining, buffers.Length);
            for (var index = 0; index < count; index++)
            {
                _frame[^1] = (byte)(_sequence + 1);
                buffers[index].SetFrame(_frame, _sequence % 2 == 0 ? NdisApiAbi.PacketFlagOnSend : NdisApiAbi.PacketFlagOnReceive, adapterHandle);
                _sequence++;
            }
            return count;
        }
    }

    /// <summary>
    /// One batch of three same-direction frames; slots 0 and 1 pass and slot 2 faults the
    /// handler, so the pump unwinds with two frames pending in the lane.
    /// </summary>
    private sealed class ScriptedFaultingReader(nint adapterHandle) : INdisPacketReader
    {
        private readonly byte[] _frame = CreateIpv4TcpFrame();
        private bool _served;

        public int TryReadPackets(nint handle, NdisPacketBuffer[] buffers)
        {
            if (_served) return 0;
            _served = true;
            for (var index = 0; index < 3; index++)
            {
                _frame[^1] = index == 2 ? (byte)0xFF : (byte)(index + 1);
                buffers[index].SetFrame(_frame, NdisApiAbi.PacketFlagOnSend, adapterHandle);
            }
            return 3;
        }
    }
}
