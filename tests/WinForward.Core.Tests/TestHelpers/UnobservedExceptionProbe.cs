namespace WinForward.Core.Tests;

/// <summary>
/// Counts <see cref="TaskScheduler.UnobservedTaskException"/> reports that reference an explicitly
/// injected exception instance. The scheduler event is process-global and test classes run in
/// parallel, so only the injected fault proves the asserting test's property — a foreign unobserved
/// task must not fail it.
/// </summary>
internal sealed class UnobservedExceptionProbe
{
    private int _count;
    private Exception? _target;

    public int Count => Volatile.Read(ref _count);

    public void Track(Exception target) => Volatile.Write(ref _target, target);

    public void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        _ = sender;
        args.SetObserved();

        var target = Volatile.Read(ref _target);
        if (target is null || Contains(args.Exception, target))
        {
            Interlocked.Increment(ref _count);
        }
    }

    /// <summary>
    /// Finalizes every unreachable task three times, which is what turns a faulted-but-unobserved task
    /// into a scheduler event. Forcing finalization is the point: without it the property under test
    /// cannot be observed at all.
    /// </summary>
    public static void ForceFinalization()
    {
#pragma warning disable S1215 // GC.Collect is required to finalize the tasks under test.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
#pragma warning restore S1215
    }

    private static bool Contains(AggregateException exception, Exception target)
    {
        foreach (var inner in exception.Flatten().InnerExceptions)
        {
            if (ReferenceEquals(inner, target))
            {
                return true;
            }
        }

        return false;
    }
}
