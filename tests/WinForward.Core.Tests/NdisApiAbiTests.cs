using System.ComponentModel;
using WinForward.NdisApi;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class NdisApiAbiTests
{
    [Fact]
    public void PinnedX64LayoutMatchesV362NonJumboHeader()
    {
        NdisApiAbi.AssertManagedX64Layout();
    }

    [Fact]
    public void IpHelperOwnerRowsMatchWindowsLayouts()
    {
        IpHelperAbi.AssertManagedLayout();
    }

    [Fact]
    public void OpenValidationRejectsOpaqueUnloadedDriverObject()
    {
        var exception = Assert.Throws<Win32Exception>(() => NdisNativeCallStatus.ThrowIfOpenFailed((nint)1, isDriverLoaded: false, nativeError: 5));

        Assert.Equal(5, exception.NativeErrorCode);
        Assert.Contains("wrapper opened", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void OpenValidationRejectsInvalidNativeHandle(long handle)
    {
        var exception = Assert.Throws<Win32Exception>(() => NdisNativeCallStatus.ThrowIfOpenFailed((nint)handle, isDriverLoaded: true, nativeError: 87));

        Assert.Equal(87, exception.NativeErrorCode);
    }

    [Fact]
    public void OpenValidationAcceptsLoadedDriverObject()
    {
        NdisNativeCallStatus.ThrowIfOpenFailed((nint)1, isDriverLoaded: true, nativeError: 0);
    }

    [Fact]
    public void DriverVersionStatusRejectsNativeFailureSentinel()
    {
        var exception = Assert.Throws<Win32Exception>(() => NdisNativeCallStatus.EnsureDriverVersion(uint.MaxValue, nativeError: 87));

        Assert.Equal(87, exception.NativeErrorCode);
        Assert.Contains("driver version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DriverVersionStatusReturnsValidVersion()
    {
        Assert.Equal(0x0003_0602u, NdisNativeCallStatus.EnsureDriverVersion(0x0003_0602u, nativeError: 0));
    }

    [Fact]
    public void QueueStatusSeparatesIdlePollingFromNativeFailures()
    {
        Assert.False(NdisNativeCallStatus.HasQueuedPackets(1, nativeError: 0, queuedPacketCount: 0, (nint)2));
        Assert.True(NdisNativeCallStatus.HasQueuedPackets(1, nativeError: 0, queuedPacketCount: 1, (nint)2));

        var exception = Assert.Throws<Win32Exception>(() => NdisNativeCallStatus.HasQueuedPackets(0, nativeError: 87, queuedPacketCount: 0, (nint)2));
        Assert.Equal(87, exception.NativeErrorCode);
        Assert.Contains("adapter 0x2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadResultRejectsFailureFromNonEmptyQueue()
    {
        Assert.True(NdisNativeCallStatus.InterpretReadResult(queuedPacketCount: 1, nativeResult: 1, nativeError: 0, (nint)2));
        Assert.False(NdisNativeCallStatus.InterpretReadResult(queuedPacketCount: 0, nativeResult: 0, nativeError: 87, (nint)2));

        var exception = Assert.Throws<Win32Exception>(() => NdisNativeCallStatus.InterpretReadResult(queuedPacketCount: 1, nativeResult: 0, nativeError: 87, (nint)2));
        Assert.Equal(87, exception.NativeErrorCode);
        Assert.Contains("non-empty queue", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PacketBufferPreservesExplicitNdisFlagsAndClearsThemForNewFrames()
    {
        using var buffer = new NdisPacketBuffer();
        buffer.SetFrame([1, 2, 3], NdisApiAbi.PacketFlagOnSend, (nint)7, flags: 0x2000_0001);

        Assert.Equal(0x2000_0001u, buffer.Flags);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer.GetFrame().ToArray());

        buffer.SetFrame([4], NdisApiAbi.PacketFlagOnReceive, (nint)8);
        Assert.Equal(0u, buffer.Flags);
    }

    [Fact]
    public void CapturedPacketUsesEnumerationHandleAndCarriesNativePacketFlags()
    {
        using var buffer = new NdisPacketBuffer();
        buffer.SetFrame([1], NdisApiAbi.PacketFlagOnSend, (nint)0x1234, flags: 0x40);
        var packet = NdisCapturedPacket.FromCapture(buffer, (nint)0x5678);

        Assert.Equal((nint)0x5678, packet.AdapterHandle);
        Assert.NotEqual(buffer.CapturedAdapterHandle, packet.AdapterHandle);
        Assert.Equal(buffer.DeviceFlags, packet.DeviceFlags);
        Assert.Equal(0x40u, packet.Flags);
    }

    [Fact]
    public void AppLocalResolverRejectsMissingNdisApiSidecar()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), $"winforward-{Guid.NewGuid():N}");

        var exception = Assert.Throws<DllNotFoundException>(() => NdisApiNative.GetApplicationLocalLibraryPath(applicationDirectory));

        Assert.Contains(Path.Combine(applicationDirectory, "ndisapi.dll"), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeCallGateSerializesConcurrentOperations()
    {
        var secondBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new NdisNativeCallGate(secondBlocked.SetResult);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Task.Run(() =>
        {
            using var gateLease = gate.Enter();
            firstEntered.SetResult();
            releaseFirst.Task.GetAwaiter().GetResult();
        });
        await firstEntered.Task;

        var second = Task.Run(() =>
        {
            using var gateLease = gate.Enter();
        });

        await secondBlocked.Task;
        Assert.Equal(1, gate.MaxConcurrentCalls);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, gate.MaxConcurrentCalls);
    }
}
