using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Lanes;
using Xunit;

namespace WinForward.E2E.Tests.Lanes;

/// <summary>
/// The receive/settle thread contract (D18.2): the receive half classifies and enqueues on whatever
/// thread its transport completes on, the send half settles on the thread the engine runs on, and the
/// identity <c>delivered == settled</c> holds with no reply lost and none booked twice. The two halves
/// really do overlap — the fake's channel is created with <c>AllowSynchronousContinuations = false</c>,
/// so a receive can never resume inline on the sending thread.
/// </summary>
public sealed class LaneEngineConcurrencyTests
{
    private const int Rounds = 4;
    private const int RepliesPerRound = 1024;

    [Fact]
    public async Task EveryReplyQueuedOnTheReceiveThreadIsSettledExactlyOnceOnTheSendThread()
    {
        for (var round = 1; round <= Rounds; round++)
        {
            await RunRoundAsync(RepliesPerRound);
        }
    }

    private static async Task RunRoundAsync(int count)
    {
        var callerThread = Environment.CurrentManagedThreadId;
        var policy = new SettlingPolicy(count);
        var transport = new ReplyStreamTransport(count);
        var options = new LaneEngineOptions
        {
            // No pace: the sends go out back to back while the receive loop drains its channel on a pool
            // thread. The fake ends the offer loop itself (see ReplyStreamTransport), so nothing here is
            // a wall-clock race; the deadline is only the engine's own backstop.
            RatePerSecond = 0,
            StartTicks = Clock.Now,
            DeadlineTicks = Clock.Now + Clock.FromSeconds(30),
            BacklogLimit = 8,
            SendBufferBytes = 16,
            ReceiveBufferBytes = 32,

            // Generous: the drain is the safety net for a starved host, since the first slot pays the
            // one-time JIT cost of the whole send path.
            DrainLimitTicks = Clock.FromSeconds(5),
        };

        var engine = new LaneEngine<ReplyStreamTransport>(transport, policy, options);
        var counts = await engine.RunAsync(CancellationToken.None);
        var bookings = string.Join(",", policy.Bookings);
        var diagnostic = $"sentOk {counts.SentOk}, policySent {policy.Sent}, sends {transport.Sends}, delivered {transport.Delivered}, settled {policy.Settled}, pending {policy.Pending}, inFlight {policy.InFlight}, bookings [{bookings}], sendThread {policy.SendThreadId}, receiveThread {policy.ReceiveThreadId}, callerThread {callerThread}";

        Assert.True(counts.SentOk == count, diagnostic);
        Assert.True(policy.Sent == count, diagnostic);
        Assert.True(transport.Delivered == count, diagnostic);
        Assert.True(policy.Settled == count, diagnostic);
        Assert.True(policy.Pending == 0, diagnostic);
        Assert.True(policy.InFlight == 0, diagnostic);
        Assert.True(policy.IsDrained, diagnostic);

        // No loss and no duplicate: every reply's sequence was booked exactly once.
        Assert.True(Array.TrueForAll(policy.Bookings, booked => booked == 1), diagnostic);

        // The send role ran on the thread this fact called the engine on, and the receive role ran at
        // all. Their thread *ids* are deliberately not compared: the calling thread is a pool worker,
        // so a receive continuation may legitimately resume on it once the send role stops. What the
        // contract actually pins is that the receive callbacks could only enqueue (they touch no book),
        // which is what the exact identity and the per-sequence bookings above assert.
        Assert.True(policy.SendThreadId == callerThread, diagnostic);
        Assert.True(policy.ReceiveThreadId != 0, diagnostic);
    }

    /// <summary>
    /// The send half: books sends in <c>OnSent</c> and replies only in <c>Settle</c>, exactly as the
    /// real policy must. <c>OnReceive</c> only classifies and enqueues — it keeps no counter at all,
    /// which is the receive-thread half of the contract (D18.5 #2).
    /// </summary>
    private sealed class SettlingPolicy : ILanePolicy
    {
        private readonly ConcurrentQueue<Settlement> _settlements = new();
        private int _sendThreadId;
        private int _receiveThreadId;
        private long _inFlight;
        private long _settled;
        private long _sent;

        internal SettlingPolicy(int count)
        {
            Bookings = new int[count];
        }

        internal long Sent => Volatile.Read(ref _sent);

        internal long Settled => Volatile.Read(ref _settled);

        internal long InFlight => Volatile.Read(ref _inFlight);

        internal int Pending => _settlements.Count;

        internal int[] Bookings { get; }

        internal int SendThreadId => Volatile.Read(ref _sendThreadId);

        internal int ReceiveThreadId => Volatile.Read(ref _receiveThreadId);

        public bool IsDrained => _settlements.IsEmpty;

        public bool BookEmpty => InFlight == 0 && Pending == 0;

        public LaneSlotDecision BuildRequest(long sequence, long intendedTicks, Span<byte> destination, out int length)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination, sequence);
            length = sizeof(long);
            return LaneSlotDecision.Send;
        }

        public void OnSent(long sequence, long intendedTicks, in LaneSendResult result)
        {
            if (result.Accepted)
            {
                Interlocked.Increment(ref _sent);
                Interlocked.Increment(ref _inFlight);
            }

            Volatile.Write(ref _sendThreadId, Environment.CurrentManagedThreadId);
        }

        public void OnReceive(in LaneReceiveResult result, ReadOnlySpan<byte> payload, long receivedTicks)
        {
            var sequence = result.Kind == LaneReceiveKind.Payload && payload.Length >= sizeof(long)
                ? BinaryPrimitives.ReadInt64LittleEndian(payload)
                : 0;
            Volatile.Write(ref _receiveThreadId, Environment.CurrentManagedThreadId);
            _settlements.Enqueue(new Settlement(sequence));
        }

        public void Settle(long nowTicks)
        {
            // Settle is the send thread's alone, so the bookings need no interlock: that single writer
            // is what the contract promises, and the booking array is the witness that it held.
            while (_settlements.TryDequeue(out var settlement))
            {
                if (settlement.Sequence >= 1 && settlement.Sequence <= Bookings.Length)
                {
                    Bookings[settlement.Sequence - 1]++;
                }

                Interlocked.Increment(ref _settled);
                if (Interlocked.Read(ref _inFlight) > 0)
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }
    }

    /// <summary>
    /// One reply per send for the reply count the constructor takes: the send half queues the reply and
    /// the receive half takes it off the channel on a pool thread. The next send ends the offer loop the
    /// way a disposed socket does — the engine's loop absorbs that — which keeps the run's token alive,
    /// so the drain still has to wait for the replies instead of the receive side being cancelled with
    /// them still queued.
    /// </summary>
    private sealed class ReplyStreamTransport : ILaneTransport
    {
        private readonly Channel<long> _replies = Channel.CreateUnbounded<long>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
        private readonly long _count;
        private long _delivered;
        private long _sends;

        internal ReplyStreamTransport(long count)
        {
            _count = count;
        }

        internal long Delivered => Volatile.Read(ref _delivered);

        internal long Sends => Volatile.Read(ref _sends);

        public ValueTask<LaneOpenResult> OpenAsync(CancellationToken cancellationToken) =>
            new(new LaneOpenResult(true, null));

        public ValueTask<LaneSendResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _sends) > _count)
            {
                throw new ObjectDisposedException(nameof(ReplyStreamTransport), "the fake ends the offer loop the way a disposed socket does");
            }

            _replies.Writer.TryWrite(BinaryPrimitives.ReadInt64LittleEndian(payload.Span));
            return new ValueTask<LaneSendResult>(new LaneSendResult(true, false, null));
        }

        public async ValueTask<LaneReceiveResult> ReceiveAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            var sequence = await _replies.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            BinaryPrimitives.WriteInt64LittleEndian(destination.Span, sequence);
            Interlocked.Increment(ref _delivered);
            return new LaneReceiveResult(LaneReceiveKind.Payload, sizeof(long));
        }

        public void Dispose()
        {
            /* the engine never disposes a transport it did not create */
        }
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct Settlement(long Sequence);
}
