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

    private readonly INdisPacketReader _driver;
    private readonly nint _adapterHandle;
    private readonly Func<NdisCapturedPacket, CancellationToken, ValueTask> _handler;
    private readonly TimeSpan _pollDelay;
    private readonly NdisPacketBuffer[] _batchBuffers;
    private int _stopped;
    private int _buffersReleased;

    public NdisCapturePump(INdisPacketReader driver, nint adapterHandle, Func<NdisCapturedPacket, CancellationToken, ValueTask> handler, TimeSpan? pollDelay = null, int? batchCapacity = null)
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
    }

    /// <summary>
    /// Pumps captured packets until cancelled or stopped. Each iteration fetches one batch
    /// (single kernel round trip) and awaits the handler for slots 0..readCount-1 strictly in
    /// order, so reinjection order within an adapter matches arrival order. An empty batch keeps
    /// the poll-delay pacing of the single-packet loop. Batch buffers live for the pump's
    /// lifetime and are released exactly once, when the run loop exits or the pump is disposed.
    /// </summary>
    public async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _stopped) == 0)
            {
                var readCount = _driver.TryReadPackets(_adapterHandle, _batchBuffers);
                if (readCount == 0)
                {
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
            }
        }
        finally
        {
            ReleaseBatchBuffers();
        }
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
