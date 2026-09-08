using System.ComponentModel;
using WinForward.NdisApi;

namespace WinForward.Core.Tests;

/// <summary>
/// An <see cref="INdisPacketReader"/> over a fixed script: each call advances to the next read
/// function (the last repeats forever), and an optional fixed throw repeats forever before any
/// read — the failure-injection seam for pump retry/degradation paths. <see cref="Calls"/>
/// exposes the advance count for poll-loop assertions.
/// </summary>
internal sealed class ScriptedReader(Func<NdisPacketBuffer[], int>[] reads, Win32Exception? throwAlways = null) : INdisPacketReader
{
    private int _calls;

    public int Calls => _calls;

    public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
    {
        if (throwAlways is not null) throw throwAlways;
        var index = Math.Min(_calls++, reads.Length - 1);
        return reads[index](buffers);
    }
}
