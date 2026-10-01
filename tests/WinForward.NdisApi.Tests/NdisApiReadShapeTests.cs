using System.ComponentModel;
using System.Runtime.Versioning;
using WinForward.NdisApi;
using Xunit;

namespace WinForward.NdisApi.Tests;

/// <summary>
/// The read shape at the driver↔native seam (research F5.1): which of the two read-path native
/// calls <see cref="NdisApiDriver"/> makes, in what order, with what requested count, and what each
/// outcome classifies as. The pump↔driver half of the read accounting already exists
/// (<see cref="CapturePumpReadCallTests"/>); this is the layer below it, reachable without
/// <c>ndisapi.dll</c> through <see cref="NdisApiDriver.CreateForTests"/>. The facts prove the
/// recorded call sequence and the classification only — never that a real IOCTL round trip
/// happened, and never which empty-queue semantics the real driver implements.
/// </summary>
public sealed class NdisApiReadShapeTests
{
    private const nint AdapterHandle = 0x2B;
    private const int Capacity = 32;
    private static readonly byte[] s_frame = new byte[64];

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SpeculativeReadIssuesOneReadAndNoQueryForANonEmptyDrain()
    {
        using var batch = new PacketBatch(Capacity);
        // The query script would report a full queue, so the fact proves the read-first drain does
        // not consult it — not merely that the script happened to be empty.
        var calls = new RecordingReadCalls([(true, 4u, 0)], [(true, 4u, 0)]);
        using var driver = NdisApiDriver.CreateForTests(calls);

        Assert.Equal(4, driver.TryReadPackets(AdapterHandle, batch.Buffers));

        Assert.Equal(1, calls.ReadCalls);
        Assert.Equal(0, calls.QueryCalls);
        Assert.Equal("[32]", calls.DescribeRequestedCounts());
        Assert.Equal("[Read]", calls.DescribeOrder());
        Assert.Equal(1, driver.ReadDiagnostics.BatchReads);
        Assert.Equal(0, driver.ReadDiagnostics.QueueSizeQueries);
        Assert.Equal(0, driver.ReadDiagnostics.EmptyReads);
        Assert.Equal(0, driver.ReadDiagnostics.FailedReads);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SpeculativeReadNeverQueriesBeforeReading()
    {
        // Four drains: a non-empty success, an empty success, a failed read the query resolves as
        // empty, and a failed read on a non-empty queue (the guard's probe). No query may precede
        // the read it disambiguates, so the only legal orders are [Read] and [Read, Query](, [Read]).
        using var batch = new PacketBatch(Capacity);
        var calls = new RecordingReadCalls([(true, 4u, 0), (true, 0u, 0), (false, 0, 31), (false, 0, 31), (true, 3u, 0)],
            [(true, 0u, 0), (true, 3u, 0)]);
        using var driver = NdisApiDriver.CreateForTests(calls);

        Assert.Equal(4, driver.TryReadPackets(AdapterHandle, batch.Buffers));
        Assert.Equal(0, driver.TryReadPackets(AdapterHandle, batch.Buffers));
        Assert.Equal(0, driver.TryReadPackets(AdapterHandle, batch.Buffers));
        Assert.Equal(3, driver.TryReadPackets(AdapterHandle, batch.Buffers));

        Assert.Equal("[Read, Read, Read, Query, Read, Query, Read]", calls.DescribeOrder());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AnEmptyQueueYieldsZeroPacketsWithNoErrorUnderEitherReadResult()
    {
        // Hypothesis B: the driver succeeds with dwPacketsSuccess == 0 — the empty queue never
        // needs a second call at all.
        using (var batch = new PacketBatch(Capacity))
        {
            var calls = new RecordingReadCalls([(true, 0u, 0)], []);
            using var driver = NdisApiDriver.CreateForTests(calls);

            Assert.Equal(0, driver.TryReadPackets(AdapterHandle, batch.Buffers));

            Assert.Equal("[Read]", calls.DescribeOrder());
            Assert.Equal(1, driver.ReadDiagnostics.BatchReads);
            Assert.Equal(0, driver.ReadDiagnostics.QueueSizeQueries);
            Assert.Equal(1, driver.ReadDiagnostics.EmptyReads);
            Assert.Equal(0, driver.ReadDiagnostics.FailedReads);
        }

        // Hypothesis A: the driver fails on an empty queue. The query is the disambiguator, and the
        // drain is still zero packets and no error — only the query's position moved.
        using (var batch = new PacketBatch(Capacity))
        {
            var calls = new RecordingReadCalls([(false, 0u, 31)], [(true, 0u, 0)]);
            using var driver = NdisApiDriver.CreateForTests(calls);

            Assert.Equal(0, driver.TryReadPackets(AdapterHandle, batch.Buffers));

            Assert.Equal("[Read, Query]", calls.DescribeOrder());
            Assert.Equal(1, driver.ReadDiagnostics.BatchReads);
            Assert.Equal(1, driver.ReadDiagnostics.QueueSizeQueries);
            Assert.Equal(1, driver.ReadDiagnostics.EmptyReads);
            Assert.Equal(1, driver.ReadDiagnostics.FailedReads);
            Assert.Equal(31, driver.ReadDiagnostics.LastFailedReadNativeError);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AReadFailureOnANonEmptyQueueWhoseGuardedProbeAlsoFailsThrowsTheReadError()
    {
        // Every read fails, so this fact holds on the query-first body, on an unguarded read-first
        // body and after the guard: the throw is the read's own native error either way.
        using var batch = new PacketBatch(Capacity);
        var calls = new RecordingReadCalls([(false, 0u, 31)], [(true, 3u, 0)]);
        using var driver = NdisApiDriver.CreateForTests(calls);

        var exception = Assert.Throws<Win32Exception>(() => driver.TryReadPackets(AdapterHandle, batch.Buffers));

        Assert.Equal(31, exception.NativeErrorCode);
        Assert.Contains("non-empty queue", exception.Message, StringComparison.Ordinal);
        // The throw names the queue depth the query reported and the request of the call that
        // actually failed — here the probe's queue-sized one.
        Assert.Contains("queued 3", exception.Message, StringComparison.Ordinal);
        Assert.Contains("requested 3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AFailedReadWithAFailedQueryStillThrowsTheQueryError()
    {
        using var batch = new PacketBatch(Capacity);
        var calls = new RecordingReadCalls([(false, 0u, 21)], [(false, 0u, 87)]);
        using var driver = NdisApiDriver.CreateForTests(calls);

        var exception = Assert.Throws<Win32Exception>(() => driver.TryReadPackets(AdapterHandle, batch.Buffers));

        Assert.Equal(87, exception.NativeErrorCode);
        Assert.Contains("Unable to inspect the NDISAPI packet queue", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ANullHoleInTheBatchArrayThrowsOnADrainThatWouldOtherwiseBeEmpty()
    {
        // The full-capacity request walks the whole caller array, so the null-slot guard is now
        // reachable on every drain rather than only on a queue the query reported non-empty. The
        // production seam is the subject here: it builds the request before it calls the driver, so
        // the guard fires without ndisapi.dll.
        using var nativeHandle = NdisApiSafeHandle.FromRawHandle(0);
        using var driver = NdisApiDriver.CreateForTests(new NdisNativeReadPacketCalls(nativeHandle));
        using var first = new NdisPacketBuffer();
        NdisPacketBuffer?[] buffers = [first, null];

        Assert.Throws<ArgumentNullException>(() => driver.TryReadPackets(AdapterHandle, buffers!));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheReadDiagnosticsCountOneReadPerDrainAndOneQueryOnlyOnAFailedRead()
    {
        using var batch = new PacketBatch(Capacity);
        var calls = new RecordingReadCalls([(true, 4u, 0), (true, 0u, 0), (false, 0, 31)], [(true, 0u, 0)]);
        using var driver = NdisApiDriver.CreateForTests(calls);

        Assert.Equal(4, driver.TryReadPackets(AdapterHandle, batch.Buffers));
        Assert.Equal(0, driver.TryReadPackets(AdapterHandle, batch.Buffers));
        Assert.Equal(0, driver.TryReadPackets(AdapterHandle, batch.Buffers));

        var diagnostics = driver.ReadDiagnostics;
        Assert.Equal(3, diagnostics.BatchReads);
        Assert.Equal(1, diagnostics.QueueSizeQueries);
        Assert.Equal(2, diagnostics.EmptyReads);
        Assert.Equal(1, diagnostics.FailedReads);
        Assert.Equal(31, diagnostics.LastFailedReadNativeError);
        Assert.Equal(0, diagnostics.ReadShapeMismatchCount);
    }

    /// <summary>
    /// The per-drain driver body — the seam dispatch, the counters, the classifier and the guard
    /// probe's lock-free lookup — must not touch the managed heap. The window contract is the
    /// suite's standard one (hot-path.md, "Allocation-gate stability"): a non-recording fake, a JIT
    /// warm-up, probe batches that each read an exactly-zero delta on an unchanged thread, the exact
    /// zero, and a thread-independent call-count backstop. <see langword="stackalloc"/> is native
    /// stack, not managed heap, so the request never appears here.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void DriverReadPathAllocatesNoManagedBytes()
    {
        const int iterations = 1_000;
        using var batch = new PacketBatch(Capacity);
        var calls = new ConformingReadCalls([4, 0]);
        using var driver = NdisApiDriver.CreateForTests(calls);

        for (var warm = 0; warm < 64; warm++) driver.TryReadPackets(AdapterHandle, batch.Buffers);

        const int maximumProbeBatches = 8;
        var stabilized = false;
        for (var probe = 0; probe < maximumProbeBatches && !stabilized; probe++)
        {
            var probeThreadId = Environment.CurrentManagedThreadId;
            var probeBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < iterations; index++) driver.TryReadPackets(AdapterHandle, batch.Buffers);
            stabilized = Environment.CurrentManagedThreadId == probeThreadId && GC.GetAllocatedBytesForCurrentThread() == probeBefore;
        }
        Assert.True(stabilized, "the driver read path never became allocation-stable");

        var measuredThreadId = Environment.CurrentManagedThreadId;
        var callsBefore = calls.ReadCalls;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++) driver.TryReadPackets(AdapterHandle, batch.Buffers);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
        Assert.Equal(0, allocated);
        Assert.Equal(iterations, calls.ReadCalls - callsBefore);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AMismatchHealsTheShapeInsteadOfThrowing()
    {
        using var batch = new PacketBatch(Capacity);
        var calls = MismatchingCalls();
        using var driver = NdisApiDriver.CreateForTests(calls);

        Assert.Equal(3, driver.TryReadPackets(AdapterHandle, batch.Buffers));

        Assert.Equal("[Read, Query, Read]", calls.DescribeOrder());
        Assert.Equal("[32, 3]", calls.DescribeRequestedCounts());
        Assert.Equal(1, driver.ReadDiagnostics.ReadShapeMismatchCount);
        Assert.True(driver.IsQueryFirstForDiagnostics(AdapterHandle));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheGuardFiresExactlyOnceAndLaterDrainsAreQueryFirst()
    {
        using var batch = new PacketBatch(Capacity);
        var calls = MismatchingCalls();
        using var driver = NdisApiDriver.CreateForTests(calls);

        Assert.Equal(3, driver.TryReadPackets(AdapterHandle, batch.Buffers));
        for (var drain = 0; drain < 3; drain++) Assert.Equal(3, driver.TryReadPackets(AdapterHandle, batch.Buffers));

        Assert.Equal("[Read, Query, Read, Query, Read, Query, Read, Query, Read]", calls.DescribeOrder());
        Assert.Equal("[32, 3, 3, 3, 3]", calls.DescribeRequestedCounts());
        Assert.Equal(1, driver.ReadDiagnostics.ReadShapeMismatchCount);
        // Four drains, five reads: the healing drain's probe is the extra one.
        Assert.Equal(5, driver.ReadDiagnostics.BatchReads);
        Assert.Equal(4, driver.ReadDiagnostics.QueueSizeQueries);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheGuardNeverFiresOnAConformingDriver()
    {
        // A thousand mixed drains — packets, a short count, and empties — all served by successful
        // reads. The guard's lookup is on the path, so "it never fires" has to be measured, not
        // assumed.
        const int drains = 1_000;
        using var batch = new PacketBatch(Capacity);
        var calls = new ConformingReadCalls([4, 2, 0]);
        using var driver = NdisApiDriver.CreateForTests(calls);

        var dispatched = 0;
        for (var drain = 0; drain < drains; drain++) dispatched += driver.TryReadPackets(AdapterHandle, batch.Buffers);

        Assert.Equal(1_000, calls.ReadCalls);
        Assert.Equal(0, calls.QueryCalls);
        // The cycle is (4, 2, 0): 333 full cycles plus the 1000th drain's 4.
        Assert.Equal(2_002, dispatched);
        var diagnostics = driver.ReadDiagnostics;
        Assert.Equal(drains, diagnostics.BatchReads);
        Assert.Equal(0, diagnostics.QueueSizeQueries);
        Assert.Equal(0, diagnostics.FailedReads);
        Assert.Equal(0, diagnostics.ReadShapeMismatchCount);
        Assert.False(driver.IsQueryFirstForDiagnostics(AdapterHandle));
    }

    /// <summary>
    /// The guard must not enter the caller's transient-retry budget: the healing read is invisible
    /// to the pump, which sees one successful batch. Without the guard the same fake degrades the
    /// adapter (five retries, ~3.1 s, then interception stops) while packets are available.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task TheMismatchNeverReachesTheTransientRetryBudget()
    {
        using var cts = new CancellationTokenSource();
        using var batch = new PacketBatch(Capacity);
        var calls = MismatchingCalls();
        using var driver = NdisApiDriver.CreateForTests(calls);
        var mismatches = new List<NdisReadShapeMismatch>();
        driver.ReadShapeMismatchSink = mismatches.Add;
        var dispatched = 0;
        await using var pump = new NdisCapturePump(driver, AdapterHandle, async (_, _) =>
        {
            // ReSharper disable once AccessToDisposedClosure // the run is awaited (Assert.ThrowsAnyAsync) before the using scope disposes cts.
            if (++dispatched == 3) await cts.CancelAsync().ConfigureAwait(false);
        }, new NdisCapturePumpOptions { PollDelay = TimeSpan.Zero, TransientRetryBaseDelay = TimeSpan.Zero, BatchCapacity = Capacity });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.RunAsync(cts.Token).AsTask()).ConfigureAwait(false);

        Assert.Equal(3, dispatched);
        Assert.Single(mismatches);
        Assert.False(pump.Diagnostics.IsDegraded);
        Assert.Equal(0, pump.Diagnostics.TransientReadRetryCount);
        Assert.Equal(0, pump.Diagnostics.TransientReadIncidentCount);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AGuardedProbeThatAlsoFailsThrowsTheReadErrorLikeToday()
    {
        using var batch = new PacketBatch(Capacity);
        var calls = new RecordingReadCalls([(false, 0u, 31)], [(true, 3u, 0)]);
        using var driver = NdisApiDriver.CreateForTests(calls);

        var exception = Assert.Throws<Win32Exception>(() => driver.TryReadPackets(AdapterHandle, batch.Buffers));

        Assert.Equal(31, exception.NativeErrorCode);
        Assert.Equal(1, driver.ReadDiagnostics.ReadShapeMismatchCount);
        Assert.True(driver.IsQueryFirstForDiagnostics(AdapterHandle));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheMismatchDiagnosticCarriesTheAdapterRequestedDepthAndError()
    {
        using var batch = new PacketBatch(Capacity);
        var calls = MismatchingCalls();
        using var driver = NdisApiDriver.CreateForTests(calls);
        var observed = default(NdisReadShapeMismatch);
        var publications = 0;
        driver.ReadShapeMismatchSink = record =>
        {
            observed = record;
            publications++;
        };

        Assert.Equal(3, driver.TryReadPackets(AdapterHandle, batch.Buffers));

        Assert.Equal(1, publications);
        Assert.Equal(AdapterHandle, observed.AdapterHandle);
        Assert.Equal(Capacity, observed.RequestedCount);
        Assert.Equal(3u, observed.QueuedPacketCount);
        Assert.Equal(31, observed.NativeError);
    }

    /// <summary>
    /// The ABI that refuses a request wider than the queue depth: every full-capacity read fails,
    /// a queue-sized read succeeds. This is the shape the guard exists for, and it is a fake
    /// because whether real hardware behaves this way is a Windows open item.
    /// </summary>
    private static RecordingReadCalls MismatchingCalls() =>
        new([(false, 0u, 31), (true, 3u, 0)], [(true, 3u, 0)]);

    /// <summary>
    /// A scripted read/query seam that records every call's name, order and requested count. The
    /// last script entry repeats, so a fact states its drain sequence once and lets the tail stay
    /// stable. Successful reads fill the slots they report, so a real pump over this fake observes
    /// frames rather than counts.
    /// </summary>
    private sealed class RecordingReadCalls(
        (bool Succeeded, uint PacketsSuccess, int Error)[] reads,
        (bool Succeeded, uint Queued, int Error)[] queries) : INdisReadPacketCalls
    {
        private readonly List<string> _order = [];
        private readonly List<int> _requestedCounts = [];
        private int _readIndex;
        private int _queryIndex;

        public int ReadCalls { get; private set; }

        public int QueryCalls { get; private set; }

        public string DescribeOrder() => $"[{string.Join(", ", _order)}]";

        public string DescribeRequestedCounts() => $"[{string.Join(", ", _requestedCounts)}]";

        public bool GetAdapterPacketQueueSize(nint adapterHandle, out uint queuedPacketCount, out int nativeError)
        {
            _order.Add("Query");
            QueryCalls++;
            // A fact that scripts no query is asserting that this shape never needs one, so a shape
            // that queries anyway must see an empty queue — never an exhausted script.
            var (succeeded, queued, error) = queries.Length == 0
                ? (Succeeded: true, Queued: 0u, Error: 0)
                : queries[Math.Min(_queryIndex++, queries.Length - 1)];
            queuedPacketCount = queued;
            nativeError = error;
            return succeeded;
        }

        public bool ReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers, int count, out uint packetsSuccess, out int nativeError)
        {
            _order.Add("Read");
            _requestedCounts.Add(count);
            ReadCalls++;
            var (succeeded, success, error) = reads[Math.Min(_readIndex++, reads.Length - 1)];
            packetsSuccess = success;
            nativeError = error;
            if (succeeded)
            {
                for (var index = 0; index < Math.Min(success, (uint)count); index++)
                {
                    buffers[index].SetFrame(s_frame, NdisApiAbi.PacketFlagOnReceive, adapterHandle);
                }
            }

            return succeeded;
        }
    }

    /// <summary>
    /// A conforming seam that allocates nothing per call, so the byte gate measures the driver
    /// rather than the probe. Read results cycle through <paramref name="successCycle"/>, which is
    /// what makes the gate cover the packet, short-count and empty drains alike.
    /// </summary>
    private sealed class ConformingReadCalls(int[] successCycle) : INdisReadPacketCalls
    {
        private long _readCalls;
        private long _queryCalls;
        private int _index;

        public long ReadCalls => Interlocked.Read(ref _readCalls);

        public long QueryCalls => Interlocked.Read(ref _queryCalls);

        public bool GetAdapterPacketQueueSize(nint adapterHandle, out uint queuedPacketCount, out int nativeError)
        {
            Interlocked.Increment(ref _queryCalls);
            queuedPacketCount = 0;
            nativeError = 0;
            return true;
        }

        public bool ReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers, int count, out uint packetsSuccess, out int nativeError)
        {
            Interlocked.Increment(ref _readCalls);
            packetsSuccess = (uint)successCycle[_index++ % successCycle.Length];
            nativeError = 0;
            return true;
        }
    }

    /// <summary>A caller array of private native buffers, released exactly once on dispose.</summary>
    private sealed class PacketBatch(int count) : IDisposable
    {
        public NdisPacketBuffer[] Buffers { get; } = Create(count);

        public void Dispose()
        {
            foreach (var buffer in Buffers) buffer.Dispose();
        }

        private static NdisPacketBuffer[] Create(int count)
        {
            var buffers = new NdisPacketBuffer[count];
            for (var index = 0; index < count; index++) buffers[index] = new NdisPacketBuffer();
            return buffers;
        }
    }
}
