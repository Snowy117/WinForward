using WinForward.NdisApi;

namespace WinForward.Core.Tests;

/// <summary>
/// A counting <see cref="INdisPacketArrivalSignal"/> that honours the requested timeout by sleeping
/// it, so an iteration driven through it takes the real wait path with a real bound instead of
/// returning instantly. Recording every timeout argument is what makes "one bounded wait per idle
/// iteration, at the configured bound" an exact assertion rather than a count of calls.
/// </summary>
internal sealed class CountingArrivalSignal : INdisPacketArrivalSignal
{
    private readonly List<TimeSpan> _observedTimeouts = [];

    public int Waits { get; private set; }

    public IReadOnlyList<TimeSpan> ObservedTimeouts => _observedTimeouts;

    public bool Wait(TimeSpan timeout)
    {
        Waits++;
        _observedTimeouts.Add(timeout);
        if (timeout > TimeSpan.Zero) Thread.Sleep(timeout);
        return false;
    }

    /// <summary>Owns no handle, so there is nothing to release.</summary>
    public void Dispose()
    {
        // Nothing to release.
    }
}
