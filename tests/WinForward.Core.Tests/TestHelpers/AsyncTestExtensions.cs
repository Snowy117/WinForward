namespace WinForward.Core.Tests;

/// <summary>Async polling and expected-cancellation helpers shared by coordinator tests.</summary>
internal static class AsyncTestExtensions
{
    /// <summary>
    /// Polls the condition every 10 ms until it holds or the timeout elapses (then throws). The
    /// default budget is deliberately generous: under full-suite parallel load the thread pool
    /// can starve queued continuations (channel reads, timer ticks, Task.Run setups) for
    /// seconds, and a tight budget turns that into false flakes — a passing condition is still
    /// observed on its first true poll, so only genuinely failing tests pay the longer budget.
    /// </summary>
    public static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Awaits a test server task, swallowing the cancellation used to shut it down.</summary>
    public static async Task IgnoreExpectedCancellationAsync(Task server)
    {
        try
        {
            await server.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            GC.KeepAlive(server);
        }
    }
}
