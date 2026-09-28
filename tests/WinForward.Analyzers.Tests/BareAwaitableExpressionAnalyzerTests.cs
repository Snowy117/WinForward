using Xunit;

namespace WinForward.Analyzers.Tests;

public sealed class BareAwaitableExpressionAnalyzerTests
{
    [Fact]
    public async Task NonAsyncBareAwaitableFiresAsync()
    {
        var test = new Test
        {
            TestCode = """
                using System.Threading.Tasks;

                static class Snippet
                {
                    public static void M()
                    {
                        [|FooAsync();|]
                    }

                    private static Task FooAsync() => Task.CompletedTask;
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AsyncBareAwaitableFiresAsync()
    {
        var test = new Test
        {
            TestCode = """
                using System.Threading.Tasks;

                static class Snippet
                {
                    public static async Task M()
                    {
                        [|FooAsync();|]
                    }

                    private static Task FooAsync() => Task.CompletedTask;
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DiscardAssignmentDoesNotFireAsync()
    {
        var test = new Test
        {
            TestCode = """
                using System.Threading.Tasks;

                static class Snippet
                {
                    private static Task? s_task;

                    public static void M()
                    {
                        _ = FooAsync();
                    }

                    private static Task FooAsync() => Task.CompletedTask;
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StoringAssignmentDoesNotFireAsync()
    {
        var test = new Test
        {
            TestCode = """
                using System.Threading.Tasks;

                static class Snippet
                {
                    private static Task? s_task;

                    public static void M()
                    {
                        s_task = FooAsync();
                        s_task ??= FooAsync();
                    }

                    private static Task FooAsync() => Task.CompletedTask;
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AwaitedWhenAnyDoesNotFireAsync()
    {
        var test = new Test
        {
            TestCode = """
                using System.Threading.Tasks;

                static class Snippet
                {
                    public static async Task M(Task first, Task second)
                    {
                        await Task.WhenAny(first, second);
                    }
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AwaitedAwaitableResultDoesNotFireAsync()
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

    private sealed class Test : LifetimeAnalyzerTest<BareAwaitableExpressionAnalyzer>;
}
