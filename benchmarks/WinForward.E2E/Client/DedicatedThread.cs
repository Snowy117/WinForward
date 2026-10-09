namespace WinForward.E2E.Client;

/// <summary>
/// Starts an async body on a thread of its own, so the caller keeps running even when the body's
/// first awaits complete synchronously: without that property a lane can run to completion inside
/// the loop that starts the lanes, and the lanes after it never start at all.
/// </summary>
internal static class DedicatedThread
{
    internal static Task RunOnOwnThreadAsync(Func<Task> body) =>
        Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();

    internal static Task<T> RunOnOwnThreadAsync<T>(Func<Task<T>> body) =>
        Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
}
