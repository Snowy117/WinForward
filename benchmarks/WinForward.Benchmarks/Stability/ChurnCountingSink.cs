using System.Diagnostics;
using WinForward.Core;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The churn wave's first-response sink: records the first response per flow id for the current wave
/// only. The payload's sequence is the wave number, so a straggler response from the previous wave
/// (arriving after <see cref="BeginWave"/>) is ignored instead of being attributed to this wave's
/// latency sample.
/// <para>
/// A response is recorded only for the flow it actually arrived on: the payload carries its sender's
/// flow id, and <see cref="InjectAsync"/>'s <c>originalFlow</c> is the flow whose relay socket the
/// reply came in on. A shared association's server writes every reply to the last client that sent
/// (<c>LoopbackSocks5UdpServer.SendReplyAsync</c> models the connection-oriented server under test),
/// so without that check a sibling's reply would be recorded as this flow's own first response.
/// </para>
/// </summary>
internal sealed class ChurnCountingSink(int flows, FlowKey[] flowKeys) : IUdpResponseSink
{
    private readonly OwnershipFlowSet _misdeliveredFlows = new(flows);
    private long[] _firstResponseTicks = new long[flows];
    private long[]? _spare;
    private long _expectedSequence;
    private long _firstResponses;

    /// <summary>How many flows recorded their own first response in the current wave.</summary>
    public long FirstResponses => Interlocked.Read(ref _firstResponses);

    /// <summary>How many of the wave's flows had their echo delivered to a different flow; never advances a timestamp.</summary>
    public long Misdelivered => _misdeliveredFlows.Count();

    public void BeginWave()
    {
        // Swap in a cleared array instead of allocating one per wave (the previous array keeps
        // any straggler's CAS out of this wave's sample; the sequence guard rejects them anyway).
        var fresh = _spare ?? new long[flows];
        _spare = _firstResponseTicks;
        Array.Clear(fresh);
        _firstResponseTicks = fresh;
        _misdeliveredFlows.Clear();
        Interlocked.Increment(ref _expectedSequence);
        Interlocked.Exchange(ref _firstResponses, 0);
    }

    /// <summary>The flow's first-response timestamp in the current wave, or null when no response was observed.</summary>
    public long? TryGetFirstResponseTicks(int flow)
    {
        var ticks = Volatile.Read(ref _firstResponseTicks[flow]);
        return ticks == 0 ? null : ticks;
    }

    public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken cancellationToken)
    {
        // ReSharper disable DuplicatedSequentialIfBodies // Guard-clause chain: parsing, wave-sequence match and flow-index bounds are independent readiness preconditions and each rejection carries its own diagnostic value; merging them into one condition obscures which precondition rejected the response.
        if (!DatagramHeader.TryRead(payload.Span, out var sequence, out var flowId)) return ValueTask.CompletedTask;
        if (sequence != Interlocked.Read(ref _expectedSequence)) return ValueTask.CompletedTask;
        if ((uint)flowId >= (uint)_firstResponseTicks.Length) return ValueTask.CompletedTask;
        // ReSharper restore DuplicatedSequentialIfBodies
        if (flowKeys[flowId] != originalFlow)
        {
            _misdeliveredFlows.Mark(flowId);
            return ValueTask.CompletedTask;
        }

        if (Interlocked.CompareExchange(ref _firstResponseTicks[flowId], Stopwatch.GetTimestamp(), 0) == 0)
        {
            Interlocked.Increment(ref _firstResponses);
        }

        return ValueTask.CompletedTask;
    }
}
