using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests.Lanes;

/// <summary>
/// The send path's performance contract over a <b>real</b> collaborator (allocation-gates.md,
/// quality-guidelines.md "Allocations"): the fake-transport gate in
/// <see cref="LaneEngineAllocationGateTests"/> measures the engine, this one measures the engine plus
/// the udp adapter's socket call on loopback. The window is opened and closed inside a decorator that
/// owns no state of its own, so what it measures is exactly the real send path; the batch's closing
/// read and the send count are asserted together, and the third fact is the counter-proof — the same
/// window over an adapter that allocates on purpose has to go red.
/// </summary>
public sealed class LaneTransportAllocationGateTests
{
    private const int BatchSize = 128;
    private const int BatchCount = 6;

    [Fact]
    public async Task TheRealUdpTransportSendPathAllocatesNoManagedBytes()
    {
        var callerThread = Environment.CurrentManagedThreadId;
        using var peer = BindLoopbackUdp(out var peerEndPoint);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var transport = new UdpLaneTransport(socket, peerEndPoint, DatagramBytes());
        using var cancellation = new CancellationTokenSource();
        var gate = new AllocationWindowTransport(transport, BatchSize, BatchCount, cancellation, allocateOnPurpose: false);

        var counts = await RunGateAsync(gate, cancellation);

        var diagnostic = $"the real udp send path allocated on every batch: batches [{string.Join(", ", gate.Batches)}], sentOk {counts.SentOk}, sendCalls {gate.SendCalls}";
        Assert.Equal(BatchSize * BatchCount, counts.SentOk);
        Assert.Equal(BatchSize * BatchCount, gate.SendCalls);

        // Readiness and the exact zero, exactly as allocation-gates.md's gate rules require: a path that never
        // becomes allocation-stable fails here rather than being averaged away.
        var batches = gate.Batches;
        var stable = Array.IndexOf(batches, 0L);
        Assert.True(stable >= 0, diagnostic);
        Assert.True(batches.Length - stable >= 4, $"only {batches.Length - stable} allocation-stable batches were measured: [{string.Join(", ", batches)}]");
        for (var batch = stable; batch < batches.Length; batch++)
        {
            Assert.Equal(0, batches[batch]);
        }

        Assert.All(gate.BatchThreadIds, threadId => Assert.Equal(callerThread, threadId));
    }

    [Fact]
    public async Task TheRealTransportGateGoesRedWhenTheAdapterAllocatesOnPurpose()
    {
        using var peer = BindLoopbackUdp(out var peerEndPoint);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var transport = new UdpLaneTransport(socket, peerEndPoint, DatagramBytes());
        using var cancellation = new CancellationTokenSource();
        var gate = new AllocationWindowTransport(transport, BatchSize, BatchCount, cancellation, allocateOnPurpose: true);

        await RunGateAsync(gate, cancellation);

        Assert.All(gate.Batches, batch => Assert.True(batch > 0, $"a batch measured {batch} bytes even though the adapter allocates on every send"));
    }

    /// <summary>
    /// A socket bound to loopback that nobody reads: sends complete into the kernel buffer and no reply
    /// ever arrives, which is the shape a measured send path needs — one thread, one call per slot.
    /// </summary>
    private static Socket BindLoopbackUdp(out IPEndPoint endPoint)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        endPoint = (IPEndPoint)socket.LocalEndPoint!;
        return socket;
    }

    private static int DatagramBytes() => FrameCodec.HeaderSize + (2 * sizeof(long)) + FrameCodec.TrailerSize;

    private static async Task<LaneCounts> RunGateAsync(AllocationWindowTransport gate, CancellationTokenSource cancellation)
    {
        var engine = new LaneEngine<AllocationWindowTransport>(gate, new FramingPolicy(), new LaneEngineOptions
        {
            RatePerSecond = 0,
            StartTicks = Clock.Now,
            DeadlineTicks = Clock.Now + Clock.FromSeconds(30),
            BacklogLimit = 4,
            SendBufferBytes = DatagramBytes(),
            ReceiveBufferBytes = DatagramBytes(),
            DrainLimitTicks = Clock.FromSeconds(0.05),
        });

        return await engine.RunAsync(cancellation.Token);
    }

    /// <summary>
    /// Frames the sequence into the engine's buffer without allocating: the window has to see the real
    /// adapter's socket call and nothing else, so the policy on the caller's side of the seam must be
    /// allocation-free too — the recording fake the engine facts use appends to lists and would be
    /// measured instead of the transport.
    /// </summary>
    private sealed class FramingPolicy : ILanePolicy
    {
        public bool IsDrained => true;

        public bool BookEmpty => false;

        public LaneSlotDecision BuildRequest(long sequence, long intendedTicks, Span<byte> destination, out int length)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination, sequence);
            length = sizeof(long);
            return LaneSlotDecision.Send;
        }

        public void OnSent(long sequence, long intendedTicks, in LaneSendResult result)
        {
            /* the gate's transport answers every send */
        }

        public void OnReceive(in LaneReceiveResult result, ReadOnlySpan<byte> payload, long receivedTicks)
        {
            /* the peer never answers, so no receive completes */
        }

        public void Settle(long nowTicks)
        {
            /* the gate's policy owns no settlement queue */
        }
    }

    /// <summary>
    /// Opens a per-thread allocation window on the first send of each batch and closes it on the last,
    /// delegating to the real adapter in between. <c>allocateOnPurpose</c> is the counter-proof: the
    /// allocation escapes into a field that <c>OnSent</c> keeps alive, the way the engine's own gate
    /// documents, because a store the JIT can elide would prove nothing.
    /// </summary>
    private sealed class AllocationWindowTransport : ILaneTransport
    {
        private readonly ILaneTransport _inner;
        private readonly int _batchSize;
        private readonly CancellationTokenSource _cancelAfter;
        private readonly bool _allocateOnPurpose;
        private byte[] _scratch = [];
        private long _openBytes;

        internal AllocationWindowTransport(ILaneTransport inner, int batchSize, int batchCount, CancellationTokenSource cancelAfter, bool allocateOnPurpose)
        {
            _inner = inner;
            _batchSize = batchSize;
            _cancelAfter = cancelAfter;
            _allocateOnPurpose = allocateOnPurpose;
            Batches = new long[batchCount];
            BatchThreadIds = new int[batchCount];
        }

        internal long SendCalls { get; private set; }

        internal long[] Batches { get; }

        internal int[] BatchThreadIds { get; }

        public ValueTask<LaneOpenResult> OpenAsync(CancellationToken cancellationToken) => _inner.OpenAsync(cancellationToken);

        public async ValueTask<LaneSendResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            var call = ++SendCalls;
            if ((call - 1) % _batchSize == 0)
            {
                _openBytes = GC.GetAllocatedBytesForCurrentThread();
                BatchThreadIds[(call - 1) / _batchSize] = Environment.CurrentManagedThreadId;
            }

            if (_allocateOnPurpose)
            {
                // The counter-proof's allocation, escaped so the JIT cannot drop it: the gate has to see
                // a real heap allocation on the very seam it measures.
                _scratch = new byte[8];
                GC.KeepAlive(_scratch);
            }

            var result = await _inner.SendAsync(payload, cancellationToken).ConfigureAwait(false);

            if (call % _batchSize == 0)
            {
                Batches[(call / _batchSize) - 1] = GC.GetAllocatedBytesForCurrentThread() - _openBytes;
            }

            if (call == _batchSize * Batches.Length)
            {
                // Cancelled after the window closed, so the cancellation's own cost is never measured.
                // ReSharper disable once MethodHasAsyncOverload // The gate needs a synchronous cancel: CancelAsync's Task and its continuation would land wherever the pool puts them, inside the next batch's measured window.
                _cancelAfter.Cancel();
            }

            return result;
        }

        public ValueTask<LaneReceiveResult> ReceiveAsync(Memory<byte> destination, CancellationToken cancellationToken) => _inner.ReceiveAsync(destination, cancellationToken);

        public void Dispose()
        {
            // The engine never disposes a transport it did not create; the test owns the real one.
        }
    }
}
