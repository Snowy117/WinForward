using System.Runtime.Versioning;
using WinForward.NdisApi;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class NdisCapturePumpTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PumpProcessesBatchInArrivalOrder()
    {
        var observed = new List<byte>();
        var handles = new List<nint>();
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedReader(
            slots =>
            {
                Fill(slots[0], 0x10);
                Fill(slots[1], 0x11);
                Fill(slots[2], 0x12);
                return 3;
            },
            _ =>
            {
                cts.Cancel();
                return 0;
            });

        await using var pump = new NdisCapturePump(reader, (nint)0x55, CaptureHandler(observed, handles), TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.RunAsync(cts.Token).AsTask());

        Assert.Equal(new byte[] { 0x10, 0x11, 0x12 }, observed);
        Assert.All(handles, handle => Assert.Equal((nint)0x55, handle));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PumpProcessesOnlyFilledSlotsOfPartialBatch()
    {
        var observed = new List<byte>();
        var handles = new List<nint>();
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedReader(
            slots =>
            {
                Fill(slots[0], 0x20);
                Fill(slots[1], 0x21);
                // Slots 2..7 stay untouched: the driver returned fewer packets than requested.
                return 2;
            },
            _ =>
            {
                cts.Cancel();
                return 0;
            });

        await using var pump = new NdisCapturePump(reader, (nint)0x66, CaptureHandler(observed, handles), TimeSpan.FromMilliseconds(1), batchCapacity: 8);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.RunAsync(cts.Token).AsTask());

        Assert.Equal(new byte[] { 0x20, 0x21 }, observed);
        Assert.Equal(2, reader.Calls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PumpPollsWhenQueueIsEmpty()
    {
        var observed = new List<byte>();
        var handles = new List<nint>();
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedReader(
            _ => 0,
            _ => 0,
            _ =>
            {
                cts.Cancel();
                return 0;
            });

        await using var pump = new NdisCapturePump(reader, (nint)0x77, CaptureHandler(observed, handles), TimeSpan.FromMilliseconds(1));

        // Two empty polls go through the poll-delay branch; the cancelled third poll surfaces as
        // the pump's normal cancellation propagation, not a failure.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.RunAsync(cts.Token).AsTask());

        Assert.Empty(observed);
        Assert.Equal(3, reader.Calls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PumpReleasesBatchBuffersExactlyOnce()
    {
        var buffers = new List<NdisPacketBuffer>();
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedReader(
            slots =>
            {
                Fill(slots[0], 0x30);
                return 1;
            },
            _ =>
            {
                cts.Cancel();
                return 0;
            });

        var pump = new NdisCapturePump(reader, (nint)0x88, (packet, _) => { buffers.Add(packet.Buffer); return ValueTask.CompletedTask; }, TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.RunAsync(cts.Token).AsTask());
        await pump.DisposeAsync();
        await pump.DisposeAsync();

        var released = Assert.Single(buffers);
        // Disposed native buffers refuse further frame access, and the double dispose stays idempotent.
        Assert.Throws<ObjectDisposedException>(() => released.GetFrame());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [SupportedOSPlatform("windows")]
    public void PumpRejectsNonPositiveBatchCapacity(int batchCapacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NdisCapturePump(new ScriptedReader(_ => 0), (nint)1, static (_, _) => ValueTask.CompletedTask, TimeSpan.FromMilliseconds(1), batchCapacity));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PumpRejectsNullDriverAndHandler()
    {
        Assert.Throws<ArgumentNullException>(() => new NdisCapturePump(null!, (nint)1, static (_, _) => ValueTask.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new NdisCapturePump(new ScriptedReader(_ => 0), (nint)1, null!));
    }

    private static Func<NdisCapturedPacket, CancellationToken, ValueTask> CaptureHandler(List<byte> observed, List<nint> handles) =>
        (packet, _) =>
        {
            observed.Add(packet.Buffer.GetFrame()[0]);
            handles.Add(packet.AdapterHandle);
            return ValueTask.CompletedTask;
        };

    private static void Fill(NdisPacketBuffer buffer, byte marker) =>
        buffer.SetFrame([marker, 0xAA, 0xBB], NdisApiAbi.PacketFlagOnReceive, (nint)0x99, flags: 0x40);

    private sealed class ScriptedReader(params Func<NdisPacketBuffer[], int>[] reads) : INdisPacketReader
    {
        private readonly Func<NdisPacketBuffer[], int>[] _reads = reads;
        private int _calls;

        public int Calls => _calls;

        public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
        {
            var index = Math.Min(_calls++, _reads.Length - 1);
            return _reads[index](buffers);
        }
    }
}
