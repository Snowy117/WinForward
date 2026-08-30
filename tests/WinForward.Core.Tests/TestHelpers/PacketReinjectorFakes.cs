using WinForward.NdisApi;
using WinForward.Runtime.Capture;

namespace WinForward.Core.Tests;

/// <summary>
/// In-memory <see cref="IPacketReinjector"/> fakes. <see cref="FakeReinjector"/> records every
/// single send (direction, adapter handle, both NDIS flag planes, last frame/buffer) and every
/// batched flush call (handle, direction, buffer identities, frames in order); the "last"
/// observables reflect the most recent send of either kind, so tests written against the
/// single-send surface keep working when sends arrive through the batched path.
/// <see cref="CountingReinjector"/> only counts calls per direction, single and batched.
/// </summary>
internal sealed class FakeReinjector : IPacketReinjector
{
    public sealed record BatchCall(nint AdapterHandle, bool ToAdapter, NdisPacketBuffer[] Buffers, byte[][] Frames);

    public int ToAdapterCount { get; private set; }
    public int ToMstcpCount { get; private set; }
    public int BatchToAdapterCount { get; private set; }
    public int BatchToMstcpCount { get; private set; }
    public nint LastAdapterHandle { get; private set; }
    public uint LastDeviceFlags { get; private set; }
    public uint LastFlags { get; private set; }
    public byte[]? LastFrame { get; private set; }
    public NdisPacketBuffer? LastBuffer { get; private set; }
    public List<BatchCall> BatchCalls { get; } = [];

    public void SendToAdapter(nint adapterHandle, NdisPacketBuffer buffer)
    {
        ToAdapterCount++;
        RecordLast(adapterHandle, buffer);
    }

    public void SendToMstcp(nint adapterHandle, NdisPacketBuffer buffer)
    {
        ToMstcpCount++;
        RecordLast(adapterHandle, buffer);
    }

    public void SendPacketsToAdapter(nint adapterHandle, NdisPacketBuffer[] buffers, int count)
    {
        BatchToAdapterCount++;
        RecordBatch(adapterHandle, toAdapter: true, buffers, count);
    }

    public void SendPacketsToMstcp(nint adapterHandle, NdisPacketBuffer[] buffers, int count)
    {
        BatchToMstcpCount++;
        RecordBatch(adapterHandle, toAdapter: false, buffers, count);
    }

    private void RecordLast(nint adapterHandle, NdisPacketBuffer buffer)
    {
        LastAdapterHandle = adapterHandle;
        LastDeviceFlags = buffer.DeviceFlags;
        LastFlags = buffer.Flags;
        LastFrame = buffer.GetFrame().ToArray();
        LastBuffer = buffer;
    }

    private void RecordBatch(nint adapterHandle, bool toAdapter, NdisPacketBuffer[] buffers, int count)
    {
        // Snapshot inside the call: the executor clears its lane slots after the batched send
        // returns, so the recorded array must not alias the caller's storage.
        var snapshot = new NdisPacketBuffer[count];
        Array.Copy(buffers, snapshot, count);
        var frames = new byte[count][];
        for (var index = 0; index < count; index++) frames[index] = buffers[index].GetFrame().ToArray();
        BatchCalls.Add(new BatchCall(adapterHandle, toAdapter, snapshot, frames));
        RecordLast(adapterHandle, buffers[count - 1]);
    }
}

internal sealed class CountingReinjector : IPacketReinjector
{
    public int SendToAdapterCount { get; private set; }
    public int SendToMstcpCount { get; private set; }
    public int BatchSendToAdapterCount { get; private set; }
    public int BatchSendToMstcpCount { get; private set; }
    public int BatchedPacketsToAdapter { get; private set; }
    public int BatchedPacketsToMstcp { get; private set; }

    public int TotalSendCalls => SendToAdapterCount + SendToMstcpCount + BatchSendToAdapterCount + BatchSendToMstcpCount;

    public void SendToAdapter(nint adapterHandle, NdisPacketBuffer buffer) => SendToAdapterCount++;

    public void SendToMstcp(nint adapterHandle, NdisPacketBuffer buffer) => SendToMstcpCount++;

    public void SendPacketsToAdapter(nint adapterHandle, NdisPacketBuffer[] buffers, int count)
    {
        BatchSendToAdapterCount++;
        BatchedPacketsToAdapter += count;
    }

    public void SendPacketsToMstcp(nint adapterHandle, NdisPacketBuffer[] buffers, int count)
    {
        BatchSendToMstcpCount++;
        BatchedPacketsToMstcp += count;
    }
}
