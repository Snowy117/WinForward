using System.ComponentModel;
using System.Runtime.Versioning;

namespace WinForward.NdisApi;

public readonly record struct NdisCapturedPacket(NdisPacketBuffer Buffer, nint AdapterHandle, uint DeviceFlags)
{
    public uint Flags { get; init; }

    public static NdisCapturedPacket FromCapture(NdisPacketBuffer buffer, nint enumerationAdapterHandle)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return new NdisCapturedPacket(buffer, enumerationAdapterHandle, buffer.DeviceFlags) { Flags = buffer.Flags };
    }
}

/// <summary>
/// The batched packet-read seam of <see cref="NdisApiDriver"/> consumed by
/// <see cref="NdisCapturePump"/>. Exposed as an interface so the pump's batch processing loop
/// (in-batch ordering, partial batches, empty-queue polling) is testable without native hardware.
/// </summary>
public interface INdisPacketReader
{
    /// <summary>
    /// Reads up to <c>buffers.Length</c> packets into the prepared buffers and returns how many
    /// were actually filled; 0 means the adapter queue was empty.
    /// </summary>
    int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers);
}

[SupportedOSPlatform("windows")]
public sealed class NdisCapturePump : IAsyncDisposable
{
    private const int DefaultBatchCapacity = 32;

    /// <summary>
    /// How many consecutive transient read failures one incident may retry before the adapter's
    /// interception degrades (R7). With the doubling base delay capped per attempt, the worst-case
    /// incident window is ~3.1 s.
    /// </summary>
    internal const int TransientRetryMaxAttempts = 5;

    private static readonly TimeSpan DefaultTransientRetryBaseDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TransientRetryDelayCap = TimeSpan.FromMilliseconds(1600);

    private readonly INdisPacketReader _driver;
    private readonly nint _adapterHandle;
    private readonly Func<NdisCapturedPacket, CancellationToken, ValueTask> _handler;
    private readonly TimeSpan _pollDelay;
    private readonly NdisPacketBuffer[] _batchBuffers;
    private readonly Action? _onBatchCompleted;
    private readonly Action<int, int>? _onTransientRetry;
    private readonly Action<int>? _onDegraded;
    private readonly TimeSpan _transientRetryBaseDelay;
    private int _stopped;
    private int _buffersReleased;
    private long _transientReadRetryCount;
    private long _transientReadIncidentCount;
    private int _transientRetryAttempt;
    private int _degraded;
    private int _lastDegradedNativeError;

    public NdisCapturePump(INdisPacketReader driver, nint adapterHandle, Func<NdisCapturedPacket, CancellationToken, ValueTask> handler, TimeSpan? pollDelay = null, int? batchCapacity = null, Action? onBatchCompleted = null, Action<int, int>? onTransientRetry = null, Action<int>? onDegraded = null, TimeSpan? transientRetryBaseDelay = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(handler);
        var capacity = batchCapacity ?? DefaultBatchCapacity;
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(batchCapacity));
        _driver = driver;
        _adapterHandle = adapterHandle;
        _handler = handler;
        _pollDelay = pollDelay ?? TimeSpan.FromMilliseconds(1);
        _batchBuffers = new NdisPacketBuffer[capacity];
        for (var index = 0; index < capacity; index++) _batchBuffers[index] = new NdisPacketBuffer();
        _onBatchCompleted = onBatchCompleted;
        _onTransientRetry = onTransientRetry;
        _onDegraded = onDegraded;
        _transientRetryBaseDelay = transientRetryBaseDelay ?? DefaultTransientRetryBaseDelay;
    }

    /// <summary>
    /// Pumps captured packets until cancelled or stopped. Each iteration fetches one batch
    /// (single kernel round trip) and awaits the handler for slots 0..readCount-1 strictly in
    /// order, so reinjection order within an adapter matches arrival order. An empty batch keeps
    /// the poll-delay pacing of the single-packet loop. The optional batch-completed callback
    /// (constructor) runs after the slot loop of every iteration — before the next read can reuse
    /// batch slots — and once more when the run loop exits, so deferred work (batched
    /// reinjection) never outlives the batch it belongs to. Batch buffers live for the pump's
    /// lifetime and are released exactly once, when the run loop exits or the pump is disposed.
    /// A transient native read failure (see <see cref="NdisNativeCallStatus.IsTransientReadError"/>)
    /// is retried with bounded exponential backoff; exhaustion or a permanent failure is a
    /// degraded exit — the loop returns normally (no throw) and the optional
    /// <c>onDegraded(nativeError)</c> callback fires exactly once, so the process and sibling
    /// adapter pumps keep running while this adapter's interception stops.
    /// </summary>
    public async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _stopped) == 0)
            {
                int readCount;
                try
                {
                    readCount = _driver.TryReadPackets(_adapterHandle, _batchBuffers);
                    // A healthy read (including an empty poll) closes the failure incident, so a
                    // later transient error starts a fresh retry budget.
                    _transientRetryAttempt = 0;
                }
                catch (Win32Exception exception)
                {
                    if (!NdisNativeCallStatus.IsTransientReadError(exception.NativeErrorCode)
                        || !await RetryTransientReadAsync(exception.NativeErrorCode, cancellationToken).ConfigureAwait(false))
                    {
                        Degrade(exception.NativeErrorCode);
                        return;
                    }

                    continue;
                }

                if (readCount == 0)
                {
                    // The every-iteration callback contract holds on empty queues too: a flush
                    // side effect must not wait for the next non-empty batch.
                    _onBatchCompleted?.Invoke();
                    await Task.Delay(_pollDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                for (var index = 0; index < readCount; index++)
                {
                    // NDISAPI contract: reinjection requests must carry the enumeration handle
                    // (GetTcpipBoundAdaptersInfo); the captured buffer's m_hAdapter is rejected
                    // by the driver with ERROR_INVALID_PARAMETER. See spec/backend/windows-ndisapi.md.
                    var packet = NdisCapturedPacket.FromCapture(_batchBuffers[index], _adapterHandle);
                    await _handler(packet, cancellationToken).ConfigureAwait(false);
                }

                _onBatchCompleted?.Invoke();
            }
        }
        finally
        {
            // Loop exit (stop, cancellation, degradation, or failure) must not strand deferred
            // work: the callback runs while the batch buffers are still valid, before their release.
            _onBatchCompleted?.Invoke();
            ReleaseBatchBuffers();
        }
    }

    /// <summary>
    /// Consumes one transient read failure against the bounded retry budget: delays with
    /// exponential backoff (cancellation-honoring) and reports whether the run should keep going.
    /// Returns false when the incident's budget is exhausted — the caller degrades.
    /// </summary>
    private async Task<bool> RetryTransientReadAsync(int nativeError, CancellationToken cancellationToken)
    {
        var attempt = _transientRetryAttempt + 1;
        if (attempt > TransientRetryMaxAttempts) return false;
        _transientRetryAttempt = attempt;
        Interlocked.Increment(ref _transientReadRetryCount);
        if (attempt == 1) Interlocked.Increment(ref _transientReadIncidentCount);
        _onTransientRetry?.Invoke(nativeError, attempt);
        await Task.Delay(TransientRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Total transient read retries attempted since construction (telemetry, R7).</summary>
    internal long TransientReadRetryCount => Interlocked.Read(ref _transientReadRetryCount);

    /// <summary>Distinct transient-failure incidents (a healthy read separates incidents; telemetry, R7).</summary>
    internal long TransientReadIncidentCount => Interlocked.Read(ref _transientReadIncidentCount);

    /// <summary>Whether this pump exited through the degraded path (telemetry, R7).</summary>
    internal bool IsDegraded => Volatile.Read(ref _degraded) != 0;

    /// <summary>The native error of the degraded exit; 0 while the pump has not degraded.</summary>
    internal int LastDegradedNativeErrorCode => Volatile.Read(ref _lastDegradedNativeError);

    private void Degrade(int nativeError)
    {
        Interlocked.Exchange(ref _lastDegradedNativeError, nativeError);
        Interlocked.Exchange(ref _degraded, 1);
        _onDegraded?.Invoke(nativeError);
    }

    private TimeSpan TransientRetryDelay(int attempt)
    {
        var delay = TimeSpan.FromTicks(_transientRetryBaseDelay.Ticks << Math.Min(attempt - 1, 20));
        return delay > TransientRetryDelayCap ? TransientRetryDelayCap : delay;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _stopped, 1);
        ReleaseBatchBuffers();
        return ValueTask.CompletedTask;
    }

    private void ReleaseBatchBuffers()
    {
        if (Interlocked.Exchange(ref _buffersReleased, 1) != 0) return;
        foreach (var buffer in _batchBuffers) buffer?.Dispose();
    }
}
