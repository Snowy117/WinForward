using System.Globalization;
using System.Text.Json;

namespace WinForward.E2E.Contracts.Json;

/// <summary>
/// The single writer of a JSONL file: one JSON object per line, serialized under one gate, and
/// flushed on a timer so a crash costs at most the records written since the last tick.
/// </summary>
/// <remarks>
/// The failure policy is a constructor argument rather than a second implementation: one writer also
/// means one record shape, which is the part of this contract that crosses between the two binaries.
/// </remarks>
public sealed class JsonlSink : IAsyncDisposable
{
    /// <summary>How often an open sink flushes itself.</summary>
    private static readonly TimeSpan s_defaultFlushInterval = TimeSpan.FromSeconds(1);

    private const int ReportEvery = 100;
    private const int BufferCapacity = 4096;

    private readonly Stream _stream;
    private readonly JsonlPolicy _policy;
    private readonly Action<Utf8JsonWriter>? _envelope;
    private readonly string _description;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MemoryStream _buffer = new(BufferCapacity);
    private readonly Utf8JsonWriter _writer;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _flushLoop;
    private long _writeErrors;
    private int _closed;

    public JsonlSink(string path, JsonlPolicy policy, Action<Utf8JsonWriter>? envelope)
        : this(path, policy, envelope, s_defaultFlushInterval)
    {
    }

    internal JsonlSink(string path, JsonlPolicy policy, Action<Utf8JsonWriter>? envelope, TimeSpan flushInterval)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(flushInterval, TimeSpan.Zero);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _description = fullPath;
        _stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.None);
        _policy = policy;
        _envelope = envelope;
        _writer = new Utf8JsonWriter(_buffer);
        _flushLoop = FlushLoopAsync(flushInterval, _shutdown.Token);
    }

    /// <summary>
    /// Writes to a stream this sink then owns: closing the sink flushes and disposes it. It exists so
    /// a test can hand the sink a stream that fails on demand.
    /// </summary>
    internal JsonlSink(Stream stream, JsonlPolicy policy, Action<Utf8JsonWriter>? envelope, TimeSpan flushInterval)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(flushInterval, TimeSpan.Zero);

        _description = "stream";
        _stream = stream;
        _policy = policy;
        _envelope = envelope;
        _writer = new Utf8JsonWriter(_buffer);
        _flushLoop = FlushLoopAsync(flushInterval, _shutdown.Token);
    }

    /// <summary>
    /// Records this sink could not write, under either policy. A caller that must not lose a record
    /// reads this after <see cref="CompleteAsync"/> rather than expecting a close to throw.
    /// </summary>
    public long WriteErrors => Interlocked.Read(ref _writeErrors);

    /// <summary>
    /// Writes one record. <paramref name="body"/> writes only that record's own properties: the sink
    /// has already opened the object and the envelope has already written the identity properties
    /// that every record of this file carries.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled before the write could start. Once it starts, the record is written
    /// whole: a token that fired between a record and its newline would leave half a line behind,
    /// which every reader of the file books as a bad line.
    /// </exception>
    public async ValueTask WriteAsync(Action<Utf8JsonWriter> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException exception)
        {
            RecordFailure(exception);
            if (_policy == JsonlPolicy.Propagate)
            {
                throw;
            }

            return;
        }

        try
        {
            Serialize(body);
            await _stream.WriteAsync(_buffer.GetBuffer().AsMemory(0, (int)_buffer.Length), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordFailure(exception);
            if (_policy == JsonlPolicy.Propagate)
            {
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Finishes the file: stops the timer, drains what is buffered and closes the stream. Called
    /// explicitly inside the caller's own failure boundary when a close failure has to be seen; a
    /// caller reads <see cref="WriteErrors"/> afterwards. Never throws, and does nothing after the
    /// first call.
    /// </summary>
    public async ValueTask CompleteAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The backstop release, including the one an <c>await using</c> performs. Never throws: a sink
    /// that could not be drained must not take a caller down on the way out.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Serializes one record into the buffer, newline included. The body runs here, in memory, so a
    /// body that throws leaves the stream untouched and the record is dropped whole instead of
    /// leaving half a line behind.
    /// </summary>
    private void Serialize(Action<Utf8JsonWriter> body)
    {
        _buffer.SetLength(0);
        _writer.Reset(_buffer);
        _writer.WriteStartObject();
        _envelope?.Invoke(_writer);
        body(_writer);
        _writer.WriteEndObject();
        _writer.Flush();
        _buffer.WriteByte((byte)'\n');
    }

    private async ValueTask CloseAsync()
    {
        // One closer: the caller's explicit CompleteAsync and the `await using` backstop both land
        // here, and only the first one owns the teardown.
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await _flushLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            /* the timer was cancelled on the way here */
        }
        catch (IOException)
        {
            /* the stream ended; nothing left to drain */
        }

        try
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await TryCloseStepAsync(() => _writer.DisposeAsync().AsTask()).ConfigureAwait(false);
            await TryCloseStepAsync(() => _stream.FlushAsync(CancellationToken.None)).ConfigureAwait(false);
            await TryCloseStepAsync(() => _stream.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task FlushLoopAsync(TimeSpan flushInterval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(flushInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RecordFailure(exception);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Counts one record the sink could not write and reports it, throttled to the first and then
    /// every hundredth failure. Never throws: the timer and the close path have no caller to fail.
    /// </summary>
    private void RecordFailure(Exception exception)
    {
        var failures = Interlocked.Increment(ref _writeErrors);
        if (failures == 1 || failures % ReportEvery == 0)
        {
            ReportToStderr(string.Create(
                CultureInfo.InvariantCulture,
                $"e2e jsonl {_description}: write failed {failures} time(s): {exception.GetType().Name}: {exception.Message}"));
        }
    }

    private async ValueTask TryCloseStepAsync(Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordFailure(exception);
        }
    }

    private static void ReportToStderr(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch (IOException)
        {
            /* stderr may be a closed pipe; a diagnostic must never end the run */
        }
        catch (ObjectDisposedException)
        {
            /* stderr may be a closed pipe; a diagnostic must never end the run */
        }
    }
}
