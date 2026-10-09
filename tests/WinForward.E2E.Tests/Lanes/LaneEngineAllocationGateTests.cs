using System.Buffers.Binary;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Lanes;
using Xunit;

namespace WinForward.E2E.Tests.Lanes;

/// <summary>
/// The send path's performance contract (DD D14.11, <c>allocation-gates.md</c>): driven on the caller's thread —
/// no <c>Dedicated</c> — a slot must allocate no managed bytes. The window is opened and closed inside
/// the transport, on the thread the engine runs on, because
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> is a per-thread counter and the send path never
/// suspends; the send count is asserted next to the byte count so the gate cannot pass by not running.
/// The second fact is the counter-proof: the same gate over a policy that allocates on purpose goes red,
/// which is what makes the green one evidence rather than a tautology.
/// </summary>
public sealed class LaneEngineAllocationGateTests
{
    private const int BatchSize = 256;
    private const int BatchCount = 6;

    [Fact]
    public async Task TheSendPathAllocatesNoManagedBytesOnTheCallersThread()
    {
        var callerThread = Environment.CurrentManagedThreadId;
        using var cancellation = new CancellationTokenSource();
        var gate = new AllocationWindowTransport(BatchSize, BatchCount, cancellation);
        var engine = new LaneEngine<AllocationWindowTransport>(gate, new FramingPolicy(allocate: false), Options());
        var counts = await engine.RunAsync(cancellation.Token);

        Assert.Equal(BatchSize * BatchCount, counts.SentOk);
        Assert.Equal(BatchSize * BatchCount, gate.SendCalls);

        // Readiness: the path has to become allocation-stable before the measured batches count, and a
        // path that never does fails here instead of being averaged away — allocation-gates.md forbids relaxing
        // the exact zero to a bound.
        var batches = gate.Batches;
        var stable = Array.IndexOf(batches, 0L);
        Assert.True(stable >= 0, $"the send path never became allocation-stable: batches [{string.Join(", ", batches)}]");
        Assert.True(batches.Length - stable >= 4, $"only {batches.Length - stable} allocation-stable batches were measured");

        // The measured half: every batch after readiness is exactly zero, and every reading came from
        // the thread this fact called the engine on.
        for (var batch = stable; batch < batches.Length; batch++)
        {
            Assert.Equal(0, batches[batch]);
        }

        Assert.All(gate.BatchThreadIds, threadId => Assert.Equal(callerThread, threadId));
    }

    [Fact]
    public async Task TheGateGoesRedWhenThePathAllocatesOnPurpose()
    {
        using var cancellation = new CancellationTokenSource();
        var gate = new AllocationWindowTransport(BatchSize, BatchCount, cancellation);
        var engine = new LaneEngine<AllocationWindowTransport>(gate, new FramingPolicy(allocate: true), Options());
        await engine.RunAsync(cancellation.Token);

        // Every batch sees the policy's deliberate allocation, which is exactly what the green fact
        // above would have missed if the window were measured in the wrong place or not at all.
        Assert.All(gate.Batches, batch => Assert.True(batch > 0, $"a batch measured {batch} bytes even though the policy allocates on every build"));
    }

    private static LaneEngineOptions Options() => new()
    {
        RatePerSecond = 0,
        StartTicks = Clock.Now,
        DeadlineTicks = Clock.Now + Clock.FromSeconds(30),
        BacklogLimit = 4,
        SendBufferBytes = 16,
        ReceiveBufferBytes = 32,
        DrainLimitTicks = Clock.FromSeconds(0.05),
    };

    /// <summary>
    /// Frames the sequence into the engine's buffer and answers <c>Send</c>. The constructor's flag adds
    /// the one deliberate allocation the counter-proof needs; both facts otherwise drive the same code,
    /// so they differ in exactly the thing under test.
    /// </summary>
    private sealed class FramingPolicy : ILanePolicy
    {
        private readonly bool _allocate;
        private byte[] _scratch = [];

        internal FramingPolicy(bool allocate)
        {
            _allocate = allocate;
        }

        public bool IsDrained => true;

        public bool BookEmpty => false;

        public LaneSlotDecision BuildRequest(long sequence, long intendedTicks, Span<byte> destination, out int length)
        {
            if (_allocate)
            {
                // The counter-proof's allocation: one small array per slot, on the send path itself.
                _scratch = new byte[8];
            }

            BinaryPrimitives.WriteInt64LittleEndian(destination, sequence);
            length = sizeof(long);
            return LaneSlotDecision.Send;
        }

        public void OnSent(long sequence, long intendedTicks, in LaneSendResult result)
        {
            // Touches the field the allocating policy writes so the store cannot be elided; KeepAlive
            // allocates nothing itself, which keeps the green fact's hot path clean.
            GC.KeepAlive(_scratch);
        }

        public void OnReceive(in LaneReceiveResult result, ReadOnlySpan<byte> payload, long receivedTicks)
        {
            /* the gate's transport never completes a receive */
        }

        public void Settle(long nowTicks)
        {
            /* the gate's policy owns no settlement queue */
        }
    }

    /// <summary>
    /// Sends synchronously — so the offer loop never leaves the caller's thread — and measures the
    /// per-thread allocation counter across fixed batches of sends, opening each batch on its first send
    /// and closing it on its last. After the batch budget is spent it cancels the run, which is what ends
    /// the offer loop; the cancellation itself lands outside every measured window.
    /// </summary>
    private sealed class AllocationWindowTransport : ILaneTransport
    {
        private readonly int _batchSize;
        private readonly CancellationTokenSource _cancelAfter;
        private long _openBytes;

        internal AllocationWindowTransport(int batchSize, int batchCount, CancellationTokenSource cancelAfter)
        {
            _batchSize = batchSize;
            Batches = new long[batchCount];
            BatchThreadIds = new int[batchCount];
            _cancelAfter = cancelAfter;
        }

        internal long SendCalls { get; private set; }

        internal long[] Batches { get; }

        internal int[] BatchThreadIds { get; }

        public ValueTask<LaneOpenResult> OpenAsync(CancellationToken cancellationToken) =>
            new(new LaneOpenResult(true, null));

        public ValueTask<LaneSendResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            var call = ++SendCalls;
            if ((call - 1) % _batchSize == 0)
            {
                _openBytes = GC.GetAllocatedBytesForCurrentThread();
                BatchThreadIds[(call - 1) / _batchSize] = Environment.CurrentManagedThreadId;
            }

            if (call % _batchSize == 0)
            {
                Batches[(call / _batchSize) - 1] = GC.GetAllocatedBytesForCurrentThread() - _openBytes;
            }

            if (call == _batchSize * Batches.Length)
            {
                _cancelAfter.Cancel();
            }

            _ = payload.Length;
            return new ValueTask<LaneSendResult>(new LaneSendResult(true, false, null));
        }

        public ValueTask<LaneReceiveResult> ReceiveAsync(Memory<byte> destination, CancellationToken cancellationToken) =>
            new(ParkAsync(cancellationToken));

        public void Dispose()
        {
            /* the engine never disposes a transport it did not create */
        }

        private static async Task<LaneReceiveResult> ParkAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new LaneReceiveResult(LaneReceiveKind.EndOfStream, 0);
        }
    }
}
