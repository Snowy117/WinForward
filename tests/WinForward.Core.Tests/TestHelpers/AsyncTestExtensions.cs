namespace WinForward.Core.Tests;

/// <summary>Async polling and expected-cancellation helpers shared by coordinator tests.</summary>
internal static class AsyncTestExtensions
{
    /// <summary>Polls the condition every 10 ms until it holds or the timeout elapses (then throws).</summary>
    public static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
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
