using WinForward.NdisApi;
using WinForward.Runtime.Capture;

namespace WinForward.Core.Tests;

/// <summary>
/// In-memory <see cref="IPacketReinjector"/> fakes. <see cref="FakeReinjector"/> records the
/// direction, adapter handle, both NDIS flag planes (device direction + captured metadata), and
/// the last frame; <see cref="CountingReinjector"/> only counts calls per direction.
/// </summary>
internal sealed class FakeReinjector : IPacketReinjector
{
    public int ToAdapterCount { get; private set; }
    public int ToMstcpCount { get; private set; }
    public nint LastAdapterHandle { get; private set; }
    public uint LastDeviceFlags { get; private set; }
    public uint LastFlags { get; private set; }
    public byte[]? LastFrame { get; private set; }
    public NdisPacketBuffer? LastBuffer { get; private set; }

    public void SendToAdapter(nint adapterHandle, NdisPacketBuffer buffer)
    {
        ToAdapterCount++;
        Record(adapterHandle, buffer);
    }

    public void SendToMstcp(nint adapterHandle, NdisPacketBuffer buffer)
    {
        ToMstcpCount++;
        Record(adapterHandle, buffer);
    }

    private void Record(nint adapterHandle, NdisPacketBuffer buffer)
    {
        LastAdapterHandle = adapterHandle;
        LastDeviceFlags = buffer.DeviceFlags;
        LastFlags = buffer.Flags;
        LastFrame = buffer.GetFrame().ToArray();
        LastBuffer = buffer;
    }
}

internal sealed class CountingReinjector : IPacketReinjector
{
    public int SendToAdapterCount { get; private set; }
    public int SendToMstcpCount { get; private set; }

    public void SendToAdapter(nint adapterHandle, NdisPacketBuffer buffer) => SendToAdapterCount++;

    public void SendToMstcp(nint adapterHandle, NdisPacketBuffer buffer) => SendToMstcpCount++;
}
