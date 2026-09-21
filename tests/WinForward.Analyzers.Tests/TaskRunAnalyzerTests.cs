using Xunit;

namespace WinForward.Analyzers.Tests;

public sealed class TaskRunAnalyzerTests
{
    [Fact]
    public async Task TaskRunFiresAsync()
    {
        var test = new Test
        {
            TestCode = """
                using System.Threading.Tasks;

                static class Snippet
                {
                    public static void M()
                    {
                        _ = [|Task.Run(() => { })|];
                    }
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TaskFactoryStartNewFiresAsync()
    {
        var test = new Test
        {
            TestCode = """
                using System.Threading.Tasks;

                static class Snippet
                {
                    public static void M()
                    {
                        _ = [|Task.Factory.StartNew(() => { })|];
                    }
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UserDefinedRunDoesNotFireAsync()
    {
        var test = new Test
        {
            TestCode = """
                static class Snippet
                {
                    public static void M(Widget widget)
                    {
                        widget.Run();
                    }
                }

                public sealed class Widget
                {
                    public void Run()
                    {
                    }
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    private sealed class Test : LifetimeAnalyzerTest<TaskRunAnalyzer>;
}
