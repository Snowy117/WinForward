using System.Runtime.InteropServices;
using WinForward.E2E.Client;

namespace WinForward.E2E.Tests.Lanes;

internal enum LaneEventKind
{
    Open,
    Receive,
    ReceiveEnded,
    Build,
    Send,
    Sent,
    Settle,
}

/// <summary>One seam callback, in the order it happened.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct LaneEvent(LaneEventKind Kind, long Sequence, long Ticks);

/// <summary>
/// The ordered record both fakes write into, so a fact can assert the order the engine drives its seam
/// in — pace before settle, settle before build, build before send, and <c>OnSent</c> before the next
/// build — instead of inferring it from counts.
/// </summary>
internal sealed class LaneEventLog
{
    private readonly Lock _gate = new();
    private readonly List<LaneEvent> _events = [];

    internal void Record(LaneEventKind kind, long sequence)
    {
        lock (_gate)
        {
            _events.Add(new LaneEvent(kind, sequence, Clock.Now));
        }
    }

    internal LaneEvent[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _events];
        }
    }
}
