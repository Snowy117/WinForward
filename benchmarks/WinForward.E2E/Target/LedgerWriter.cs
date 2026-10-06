using System.Globalization;
using System.Text.Json;

namespace WinForward.E2E.Target;

internal sealed class LedgerWriter : IAsyncDisposable
{
    private static readonly TimeSpan s_flushInterval = TimeSpan.FromSeconds(1);

    private readonly FileStream _stream;
    private readonly string _label;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _flushLoop;
    private long _writeErrors;
    private bool _disposed;

    internal LedgerWriter(string path, string label)
    {
        _label = label;
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.None);
        _flushLoop = FlushLoopAsync(_shutdown.Token);
    }

    /// <summary>Records that could not be written; the ledger observes the run and must never end it.</summary>
    internal long WriteErrors => Interlocked.Read(ref _writeErrors);

    /// <summary>
    /// Writes one record. <paramref name="body"/> writes only the record's own properties: the writer is
    /// already inside the record object. Every record carries the absolute time and the label the target was
    /// started with, because the target's own stopwatch cannot be aligned with the client's clock without
    /// them and a ledger row cannot be attributed to an arm without a caller-supplied label.
    /// </summary>
    internal async ValueTask WriteAsync(Action<Utf8JsonWriter> body, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var buffer = new MemoryStream(512);
            await using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("label", _label);
                body(writer);
                writer.WriteEndObject();
            }

            buffer.WriteByte((byte)'\n');
            await _stream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            /* a full or dead disk must not take the target down before its final summaries are written */
            RecordFailure(exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await _flushLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (IOException)
        {
            /* the stream ended; nothing left to drain */
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await TryAsync(_stream.FlushAsync(CancellationToken.None)).ConfigureAwait(false);
            await TryAsync(_stream.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _shutdown.Dispose();
        }
    }

    private void RecordFailure(Exception exception)
    {
        var failures = Interlocked.Increment(ref _writeErrors);
        if (failures == 1 || failures % 100 == 0)
        {
            ReportToStderr(string.Create(
                CultureInfo.InvariantCulture,
                $"e2e target: ledger write failed {failures} time(s): {exception.GetType().Name}: {exception.Message}"));
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
            /* stderr may be a closed pipe; a diagnostic must never end the target */
        }
        catch (ObjectDisposedException)
        {
            /* stderr may be a closed pipe; a diagnostic must never end the target */
        }
    }

    private static async ValueTask TryAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportToStderr($"e2e target: ledger close failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(s_flushInterval);
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
}
