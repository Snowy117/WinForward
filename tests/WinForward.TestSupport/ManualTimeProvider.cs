namespace WinForward.TestSupport;

/// <summary>
/// A clock a test moves by hand and whose timers never fire: <see cref="TimeProvider.GetUtcNow"/> is
/// exactly what the test set, and <see cref="CreateTimer"/> hands out an inert timer so a worker
/// driven by <c>PeriodicTimer</c> only runs when the test calls its tick. The production clock path is
/// unchanged — the cache reads only these two members.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset _now = initial;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan duration) => _now = _now.Add(duration);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new InertTimer();

    private sealed class InertTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
