using System.ComponentModel;
using WinForward.NdisApi;

namespace WinForward.TestSupport;

/// <summary>
/// An <see cref="INdisPacketReader"/> over a fixed script: each call advances to the next read
/// function (the last repeats forever), and an optional fixed throw repeats forever before any
/// read — the failure-injection seam for pump retry/degradation paths. <see cref="Calls"/>
/// exposes the advance count for poll-loop assertions.
/// </summary>
internal sealed class ScriptedReader(Func<NdisPacketBuffer[], int>[] reads, Win32Exception? throwAlways = null) : INdisPacketReader
{

    public int Calls { get; private set; }

    public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
    {
        if (throwAlways is not null) throw throwAlways;
        var index = Math.Min(Calls++, reads.Length - 1);
        return reads[index](buffers);
    }
}

/// <summary>
/// A scripted reader that records every call's returned count into pre-sized storage, so the counting
/// itself allocates nothing: the pump's read-call and idle-allocation gates measure the pump rather
/// than the probe.
/// </summary>
internal sealed class CountingCaptureReader(Func<nint, NdisPacketBuffer[], int>[] script) : INdisPacketReader
{
    private readonly int[] _observed = new int[256];

    public int ReadCalls { get; private set; }

    public int EmptyReads { get; private set; }

    public int BatchReads { get; private set; }

    public long PacketsReturned { get; private set; }

    public int[] ObservedCounts() => _observed[..ReadCalls];

    public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
    {
        var index = Math.Min(ReadCalls, script.Length - 1);
        var count = script[index](adapterHandle, buffers);
        if (ReadCalls < _observed.Length) _observed[ReadCalls] = count;
        ReadCalls++;
        if (count == 0)
        {
            EmptyReads++;
        }
        else
        {
            BatchReads++;
            PacketsReturned += count;
        }

        return count;
    }
}
