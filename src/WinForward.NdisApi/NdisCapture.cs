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

[SupportedOSPlatform("windows")]
public sealed class NdisCapturePump : IAsyncDisposable
{
    private readonly NdisApiDriver _driver;
    private readonly nint _adapterHandle;
    private readonly Func<NdisCapturedPacket, CancellationToken, ValueTask> _handler;
    private readonly TimeSpan _pollDelay;
    private int _stopped;

    public NdisCapturePump(NdisApiDriver driver, nint adapterHandle, Func<NdisCapturedPacket, CancellationToken, ValueTask> handler, TimeSpan? pollDelay = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(handler);
        _driver = driver;
        _adapterHandle = adapterHandle;
        _handler = handler;
        _pollDelay = pollDelay ?? TimeSpan.FromMilliseconds(1);
    }

    public async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _stopped) == 0)
        {
            using var buffer = new NdisPacketBuffer();
            if (!_driver.TryReadPacket(_adapterHandle, buffer))
            {
                await Task.Delay(_pollDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // NDISAPI contract: reinjection requests must carry the enumeration handle
            // (GetTcpipBoundAdaptersInfo); the captured buffer's m_hAdapter is rejected
            // by the driver with ERROR_INVALID_PARAMETER. See spec/backend/windows-ndisapi.md.
            var packet = NdisCapturedPacket.FromCapture(buffer, _adapterHandle);
            await _handler(packet, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _stopped, 1);
        return ValueTask.CompletedTask;
    }
}
