using Xunit;

namespace WinForward.Analyzers.Tests;

public sealed class UnawaitedAwaitableDiscardAnalyzerTests
{
    [Fact]
    public async Task TaskDiscardFiresAsync()
    {
        var test = new Test
        {
            TestCode = """
                using System.Threading.Tasks;

                static class Snippet
                {
                    public static void M()
                    {
                        [|_ = FooAsync()|];
                    }

                    private static Task FooAsync() => Task.CompletedTask;
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EmbeddedDiscardFiresAsync()
    {
        var test = new Test
        {
            TestCode = """
                using System.Threading.Tasks;

                static class Snippet
                {
                    public static void M(bool condition)
                    {
                        if (condition) [|_ = FooAsync()|];
                    }

                    private static Task FooAsync() => Task.CompletedTask;
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CustomAwaitableDiscardFiresAsync()
    {
        var test = new Test
        {
            TestCode = """
                using System;
                using System.Runtime.CompilerServices;

                static class Snippet
                {
                    public static void M()
                    {
                        [|_ = CustomAwaitable()|];
                    }

                    private static CustomAwaitable CustomAwaitable() => default;
                }

                public readonly struct CustomAwaitable
                {
                    public CustomAwaiter GetAwaiter() => default;
                }

                public readonly struct CustomAwaiter : INotifyCompletion
                {
                    public bool IsCompleted => true;

                    public void OnCompleted(Action continuation)
                    {
                    }

                    public void GetResult()
                    {
                    }
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AwaitedDiscardDoesNotFireAsync()
    {
        var test = new Test
        {
            TestCode = """
                using System.Threading.Tasks;

                static class Snippet
                {
                    public static async Task M()
                    {
                        _ = await FooAsync();
                    }

                    private static Task<int> FooAsync() => Task.FromResult(0);
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AggregateExceptionDiscardDoesNotFireAsync()
    {
        var test = new Test
        {
            TestCode = """
                using System.Threading.Tasks;

                static class Snippet
                {
                    public static void M()
                    {
                        var task = Task.CompletedTask;
                        _ = task.Exception;
                    }
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NonAwaitableDiscardDoesNotFireAsync()
    {
        var test = new Test
        {
            TestCode = """
                using System.Threading;

                static class Snippet
                {
                    private static int s_counter;

                    public static void M(char character)
                    {
                        _ = TryWrite();
                        _ = Interlocked.Add(ref s_counter, 1);
                        _ = character switch
                        {
                            'a' => "a",
                            _ => null,
                        };
                    }

                    private static bool TryWrite() => true;
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    private sealed class Test : LifetimeAnalyzerTest<UnawaitedAwaitableDiscardAnalyzer>;
}
