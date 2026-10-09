namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The set of flow ids an ownership-aware response sink saw a <em>foreign</em> delivery for: one bit
/// per flow, set by <see cref="Mark"/>, counted once by <see cref="Count"/>. The sinks key the mark by
/// the reply's <em>sender</em>, so misdelivery is a per-flow fact rather than a per-reply count: a
/// flow whose echo landed on a sibling is one flow the wave failed to answer, however many foreign
/// replies it also received. That is what lets the identities <c>own + noResponse == flows</c> (no
/// misdelivery) and <c>own + misdelivered + noResponse == flows</c> (sharing) read straight off the
/// emitted row.
/// </summary>
internal sealed class OwnershipFlowSet(int flowCapacity)
{
    private const int Shift = 6;
    private const int Mask = (1 << Shift) - 1;

    private readonly ulong[] _bits = new ulong[((flowCapacity - 1) >> Shift) + 1];

    /// <summary>Records that a reply sent by <paramref name="flowId"/> arrived on a different flow, and reports whether this call is the one that set its bit. Safe to call from any flow's receive loop.</summary>
    public bool Mark(int flowId)
    {
        var bit = 1UL << (flowId & Mask);
        ref var word = ref _bits[flowId >> Shift];
        var previous = Interlocked.Or(ref word, bit);
        return (previous & bit) == 0;
    }

    /// <summary>Clears <paramref name="flowId"/>'s mark and reports whether it was set.</summary>
    public bool Unmark(int flowId)
    {
        var bit = 1UL << (flowId & Mask);
        ref var word = ref _bits[flowId >> Shift];
        var previous = Interlocked.And(ref word, ~bit);
        return (previous & bit) != 0;
    }

    /// <summary>How many flows received at least one foreign reply.</summary>
    public int Count()
    {
        var marked = 0;
        foreach (var word in _bits)
        {
            marked += System.Numerics.BitOperations.PopCount(word);
        }

        return marked;
    }

    /// <summary>Clears every mark for the next measurement wave.</summary>
    public void Clear() => Array.Clear(_bits);
}
