using WinForward.Runtime;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;

namespace WinForward.Core.Tests;

public sealed class QuiescenceScopeTests
{
    [Fact]
    public void EnterAndExitRestoreIdleAccounting()
    {
        var scope = new QuiescenceScope();

        Assert.True(scope.IsIdle);
        Assert.True(scope.TryEnter(out var first));
        Assert.True(scope.TryEnter(out var second));
        Assert.False(scope.IsIdle);

        first.Dispose();
        Assert.False(scope.IsIdle);

        second.Dispose();
        Assert.True(scope.IsIdle);
    }

    [Fact]
    public async Task DoubleDisposeOfOneLeaseLeavesTheCountIntact()
    {
        var scope = new QuiescenceScope();
        Assert.True(scope.TryEnter(out var lease));

        lease.Dispose();
        lease.Dispose();

        Assert.True(scope.IsIdle);
        await scope.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DrainWaitsForEveryOutstandingLease()
    {
        var scope = new QuiescenceScope();
        Assert.True(scope.TryEnter(out var first));
        Assert.True(scope.TryEnter(out var second));

        var drain = scope.DrainAsync();
        Assert.False(drain.IsCompleted);

        first.Dispose();
        await Task.Delay(50);
        Assert.False(drain.IsCompleted);

        second.Dispose();
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(scope.IsIdle);
    }

    [Fact]
    public async Task DrainOnIdleScopeCompletes()
    {
        var scope = new QuiescenceScope();
        await scope.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(scope.IsIdle);
    }

    [Fact]
    public async Task SealedScopeRefusesEnterAndRunWithoutInvokingTheBody()
    {
        var scope = new QuiescenceScope();
        await scope.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(scope.TryEnter(out _));

        var invoked = false;
        Assert.False(scope.Run(
            _ =>
            {
                invoked = true;
                return Task.CompletedTask;
            },
            "sealed.child"));
        Assert.False(invoked);
    }

    [Fact]
    public async Task DrainIsSingleFlightAndIdempotent()
    {
        var scope = new QuiescenceScope();
        Assert.True(scope.TryEnter(out var lease));

        var first = scope.DrainAsync();
        var second = scope.DrainAsync();
        Assert.Same(second, scope.DrainAsync());

        lease.Dispose();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        var again = scope.DrainAsync();
        Assert.Same(second, again);
        Assert.True(again.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DrainCallersObserveTheSameTaskInstance()
    {
        var scope = new QuiescenceScope();
        Assert.True(scope.TryEnter(out var lease));

        var first = scope.DrainAsync();
        var second = scope.DrainAsync();
        Assert.True(ReferenceEquals(first, second));

        lease.Dispose();
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(ReferenceEquals(first, scope.DrainAsync()));
    }

    [Fact]
    public async Task ConcurrentDrainCallersObserveTheSameTaskInstance()
    {
        for (var round = 0; round < 16; round++)
        {
            var scope = new QuiescenceScope();
            Assert.True(scope.TryEnter(out var lease));

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workers = new Task[Environment.ProcessorCount];
            var drains = new Task[workers.Length];
            for (var index = 0; index < workers.Length; index++)
            {
                var slot = index;
                workers[index] = Task.Run(async () =>
                {
                    await gate.Task.ConfigureAwait(false);
                    drains[slot] = scope.DrainAsync();
                });
            }

            gate.SetResult();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.All(drains, drain => Assert.True(ReferenceEquals(drains[0], drain)));

            lease.Dispose();
            await drains[0].WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DrainCancelsTheOwnedToken()
    {
        var scope = new QuiescenceScope();
        var token = scope.Token;
        Assert.False(token.IsCancellationRequested);

        await scope.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task TokenIsReleasedOnceDrained()
    {
        var scope = new QuiescenceScope();
        await scope.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Throws<ObjectDisposedException>(() => scope.Token);
    }

    [Fact]
    public async Task CancelAfterDrainIsANoOp()
    {
        var scope = new QuiescenceScope();
        await scope.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        scope.Cancel();
    }

    [Fact]
    public async Task RecordFaultKeepsTheFirstExceptionAndDoesNotFaultTheDrain()
    {
        var scope = new QuiescenceScope();
        var first = new InvalidOperationException("first");
        var second = new IOException("second");

        scope.RecordFault(first, "first.site");
        scope.RecordFault(second, "second.site");

        Assert.Same(first, scope.Fault);
        Assert.Equal("first.site", scope.FaultSite);
        await scope.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(first, scope.Fault);
        Assert.Equal("first.site", scope.FaultSite);
    }

    [Fact]
    public void RecordFaultWithoutASiteLeavesTheFaultSiteEmpty()
    {
        var scope = new QuiescenceScope();

        scope.RecordFault(new InvalidOperationException("boom"));

        Assert.Null(scope.FaultSite);
    }

    [Fact]
    public async Task FaultedRunChildRecordsItsName()
    {
        var scope = new QuiescenceScope();
        var fault = new InvalidOperationException("boom");

        Assert.True(scope.Run(_ => throw fault, "udp.receive-failure"));
        await WaitForAsync(() => scope.Fault is not null);

        Assert.Same(fault, scope.Fault);
        Assert.Equal("udp.receive-failure", scope.FaultSite);

        await scope.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ChildFaultDoesNotCancelSiblingsAndDrainStillCompletes()
    {
        var scope = new QuiescenceScope();
        Assert.True(scope.TryEnter(out var sibling));

        var fault = new InvalidOperationException("child");
        Assert.True(scope.Run(_ => throw fault, "faulted.child"));
        await WaitForAsync(() => scope.Fault is not null);
        Assert.Same(fault, scope.Fault);

        // The sibling lease survives the fault and can still be released normally.
        Assert.False(scope.IsIdle);
        sibling.Dispose();
        await WaitForAsync(() => scope.IsIdle);

        // The drain still completes despite the recorded fault.
        await scope.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(fault, scope.Fault);
    }

    [Fact]
    public async Task FaultedRunChildIsRecordedAndDoesNotEscape()
    {
        var probe = new UnobservedExceptionProbe();
        TaskScheduler.UnobservedTaskException += probe.OnUnobserved;
        try
        {
            var scope = new QuiescenceScope();
            var fault = new InvalidOperationException("boom");
            probe.Track(fault);

            Assert.True(scope.Run(_ => throw fault, "faulted.child"));
            await WaitForAsync(() => scope.Fault is not null);
            Assert.Same(fault, scope.Fault);

            await scope.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

            // Forcing finalization is the point of this test: the discarded child task must be
            // collected to prove its swallowed fault never becomes an unobserved task exception.
#pragma warning disable S1215 // GC.Collect is required to finalize the discarded child task.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
#pragma warning restore S1215

            Assert.Equal(0, probe.Count);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= probe.OnUnobserved;
        }
    }

    [Fact]
    public async Task NestedScopesDrainByComposition()
    {
        var parent = new QuiescenceScope();
        var child = new QuiescenceScope(parent.Token);

        Assert.True(parent.TryEnter(out var parentLease));
        Assert.True(child.TryEnter(out var childLease));

        var parentDrain = parent.DrainAsync();
        Assert.False(parentDrain.IsCompleted);
        await WaitForAsync(() => child.Token.IsCancellationRequested);

        childLease.Dispose();
        await child.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        parentLease.Dispose();
        await parentDrain.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(parent.IsIdle);
    }

    [Fact]
    public async Task ConcurrentEnterExitRacingDrainNeverLosesTheWakeup()
    {
        for (var round = 0; round < 32; round++)
        {
            var scope = new QuiescenceScope();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workers = new Task[Environment.ProcessorCount * 2];
            for (var index = 0; index < workers.Length; index++)
            {
                workers[index] = Task.Run(async () =>
                {
                    await start.Task.ConfigureAwait(false);
                    while (scope.TryEnter(out var lease))
                    {
                        lease.Dispose();
                    }
                });
            }

            start.SetResult();
            await scope.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(scope.IsIdle);
        }
    }

    private sealed class UnobservedExceptionProbe
    {
        private int _count;
        private Exception? _target;

        public int Count => Volatile.Read(ref _count);

        public void Track(Exception target) => Volatile.Write(ref _target, target);

        public void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            _ = sender;
            args.SetObserved();

            // The scheduler event is process-global and other test classes run in parallel, so only
            // the injected fault proves this test's property; a foreign unobserved task must not fail it.
            var target = Volatile.Read(ref _target);
            if (target is null || Contains(args.Exception, target))
            {
                Interlocked.Increment(ref _count);
            }
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
}
