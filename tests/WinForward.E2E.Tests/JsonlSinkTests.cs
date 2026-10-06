using System.Text;
using WinForward.E2E.Contracts.Json;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The sink's two failure policies and its record lifecycle, driven through a stream that fails on
/// demand and one that blocks inside a write. Both sides of the harness share this writer, so the
/// tests below are the contract that keeps a lost record from being silent on one side and fatal on
/// the other.
/// </summary>
public sealed class JsonlSinkTests
{
    // Long enough that the periodic flush never fires inside a test that is not about flushing, so
    // a failure count can be asserted exactly.
    private static readonly TimeSpan s_noFlush = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan s_fastFlush = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task ABodyThatThrowsIsPropagatedUnderTheClientPolicy()
    {
        using var stream = new MemoryStream();
        await using var sink = new JsonlSink(stream, JsonlPolicy.Propagate, envelope: null, s_noFlush);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sink.WriteAsync(static _ => throw new InvalidOperationException("body"), CancellationToken.None));

        Assert.Equal(1, sink.WriteErrors);
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public async Task AnIoFailureIsPropagatedUnderTheClientPolicy()
    {
        var stream = new FailingStream();
        await using var sink = new JsonlSink(stream, JsonlPolicy.Propagate, envelope: null, s_noFlush);

        await Assert.ThrowsAsync<IOException>(
            async () => await sink.WriteAsync(static writer => writer.WriteNumber("value", 1), CancellationToken.None));

        Assert.Equal(1, sink.WriteErrors);
    }

    // The counter-target of "close never throws" is the writer this sink replaced: JsonlFile threw a
    // close failure out of DisposeAsync, which is how a full disk killed a client run after its last
    // record instead of failing the arm that owned the file. Both policies now book it and return.
    [Fact]
    public async Task ACloseFailureIsCountedAndNeverThrown()
    {
        var stream = new FailingStream();
        var sink = new JsonlSink(stream, JsonlPolicy.Propagate, envelope: null, s_noFlush);

        await sink.CompleteAsync();
        await sink.DisposeAsync();

        Assert.True(sink.WriteErrors > 0, "the close failure was not counted");
    }

    [Fact]
    public async Task TheTargetPolicyNeverThrowsAndCountsEveryFailure()
    {
        var stream = new FailingStream();
        var sink = new JsonlSink(stream, JsonlPolicy.SwallowAndCount, envelope: null, s_noFlush);

        await sink.WriteAsync(static _ => throw new InvalidOperationException("body"), CancellationToken.None);
        await sink.WriteAsync(static writer => writer.WriteNumber("value", 1), CancellationToken.None);
        Assert.Equal(2, sink.WriteErrors);

        await sink.CompleteAsync();
        Assert.True(sink.WriteErrors > 2, "the close failure was not counted");
    }

    [Fact]
    public async Task ABodyThatThrowsLeavesNoPartialLineAndTheNextRecordIsIntact()
    {
        using var stream = new MemoryStream();
        await using var sink = new JsonlSink(stream, JsonlPolicy.SwallowAndCount, envelope: null, s_noFlush);

        await sink.WriteAsync(
            static writer =>
            {
                writer.WriteString("type", "broken");
                throw new InvalidOperationException("body");
            },
            CancellationToken.None);
        await sink.WriteAsync(static writer => writer.WriteString("type", "good"), CancellationToken.None);

        // A body that throws halfway through must not reach the file: the record is serialized into
        // memory first, so the sink can drop it whole. The second record also proves the writer
        // recovers from the half-open object the first body left behind.
        Assert.Equal("{\"type\":\"good\"}\n", Encoding.UTF8.GetString(stream.ToArray()));
        Assert.Equal(1, sink.WriteErrors);
    }

    [Fact]
    public async Task ACancellationBeforeTheRecordIsTakenIsHonoured()
    {
        using var stream = new MemoryStream();
        await using var sink = new JsonlSink(stream, JsonlPolicy.Propagate, envelope: null, s_noFlush);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await sink.WriteAsync(static writer => writer.WriteNumber("value", 1), cancellation.Token));

        // A cancellation is not a failed record: nothing was written and nothing was counted.
        Assert.Equal(0, sink.WriteErrors);
        Assert.Empty(stream.ToArray());
    }

    // The counter-target here is any sink that passes the caller's token into the stream write: a
    // cancellation landing between the record and its newline leaves a half line in the file, which
    // every reader books as a bad line. This test cancels while the record is inside the write and
    // requires the record to come out whole.
    [Fact]
    public async Task ACancellationDuringTheWriteDoesNotCutTheRecordInHalf()
    {
        var stream = new GatedStream();
        var sink = new JsonlSink(stream, JsonlPolicy.Propagate, envelope: null, s_noFlush);
        using var cancellation = new CancellationTokenSource();

        var write = sink.WriteAsync(
            static writer =>
            {
                writer.WriteString("type", "probe");
                writer.WriteNumber("value", 1);
            },
            cancellation.Token).AsTask();

        await stream.Entered;
        await cancellation.CancelAsync();
        stream.Release();
        await write;

        Assert.Equal("{\"type\":\"probe\",\"value\":1}\n", Encoding.UTF8.GetString(stream.ToArray()));
        Assert.Equal(0, sink.WriteErrors);
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task ARecordIsFlushedWhileTheSinkIsStillOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wf-e2e-sink-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var sink = new JsonlSink(path, JsonlPolicy.Propagate, envelope: null, s_fastFlush))
            {
                await sink.WriteAsync(static writer => writer.WriteString("type", "probe"), CancellationToken.None);

                // Counter-target: the writer this sink replaced only wrote to the file when the arm
                // ended, so a crash mid-arm left a 0-byte file and the arm's whole sample series was
                // lost (D1/D2/D4). This loop never sees those bytes and fails.
                var deadline = Environment.TickCount64 + 5000;
                while (Environment.TickCount64 < deadline && new FileInfo(path).Length == 0)
                {
                    await Task.Delay(10, CancellationToken.None);
                }
            }

            Assert.Equal("{\"type\":\"probe\"}\n", await File.ReadAllTextAsync(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The timer is the one writer with no caller to fail, so a flush it cannot complete has to be
    // counted instead of thrown: the arm reads the count after its last record and fails on it,
    // which is how a disk that dies mid-arm still ends the arm rather than the whole run.
    [Theory]
    [InlineData(JsonlPolicy.Propagate)]
    [InlineData(JsonlPolicy.SwallowAndCount)]
    public async Task APeriodicFlushFailureIsCountedAndNeverThrown(JsonlPolicy policy)
    {
        var stream = new FlushFailingStream();
        var sink = new JsonlSink(stream, policy, envelope: null, s_fastFlush);
        await sink.WriteAsync(static writer => writer.WriteString("type", "probe"), CancellationToken.None);

        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && sink.WriteErrors == 0)
        {
            await Task.Delay(10, CancellationToken.None);
        }

        Assert.True(sink.WriteErrors > 0, "the periodic flush failure was not counted");
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task AWriteAfterTheFirstCloseIsCountedUnderTheTargetPolicy()
    {
        using var stream = new MemoryStream();
        var sink = new JsonlSink(stream, JsonlPolicy.SwallowAndCount, envelope: null, s_noFlush);
        await sink.DisposeAsync();

        await sink.WriteAsync(static writer => writer.WriteNumber("value", 1), CancellationToken.None);

        // The gate is owned by the sink and goes away with it; a late writer is booked rather than
        // left with an ObjectDisposedException the target's policy would have to survive.
        Assert.Equal(1, sink.WriteErrors);
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public async Task AWriteAfterTheFirstCloseStillFailsTheClientPolicyCaller()
    {
        using var stream = new MemoryStream();
        var sink = new JsonlSink(stream, JsonlPolicy.Propagate, envelope: null, s_noFlush);
        await sink.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await sink.WriteAsync(static writer => writer.WriteNumber("value", 1), CancellationToken.None));

        Assert.Equal(1, sink.WriteErrors);
    }

    [Fact]
    public async Task TheEnvelopeIsWrittenBeforeTheBodyOfEveryRecord()
    {
        using var stream = new MemoryStream();
        await using var sink = new JsonlSink(
            stream,
            JsonlPolicy.SwallowAndCount,
            static writer => writer.WriteString("label", "probe"),
            s_noFlush);

        await sink.WriteAsync(static writer => writer.WriteNumber("value", 7), CancellationToken.None);

        Assert.Equal("{\"label\":\"probe\",\"value\":7}\n", Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>A stream whose writes and flushes fail the way a full or dead disk does.</summary>
    private sealed class FailingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("injected write failure");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("injected write failure");

        public override void Flush() => throw new IOException("injected flush failure");

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(new IOException("injected flush failure"));
    }

    /// <summary>
    /// A stream whose writes succeed and whose flush fails, so the periodic timer is provably the
    /// only thing that can book the failure: a writable FileStream buffers until it flushes too.
    /// </summary>
    private sealed class FlushFailingStream : MemoryStream
    {
        public override void Flush() => throw new IOException("injected flush failure");

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(new IOException("injected flush failure"));
    }

    /// <summary>
    /// A stream that parks inside its write until the test releases it, so a cancellation can be made
    /// to land while a record is provably being written. It ignores the token it is handed, exactly
    /// as a FileStream write does once the call is in progress.
    /// </summary>
    private sealed class GatedStream : MemoryStream
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Release() => _release.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
