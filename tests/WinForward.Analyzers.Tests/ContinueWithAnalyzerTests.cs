using Xunit;

namespace WinForward.Analyzers.Tests;

public sealed class ContinueWithAnalyzerTests
{
    [Fact]
    public async Task TaskReceiverContinueWithFiresAsync()
    {
        var test = new Test
        {
            TestCode = """
                using System.Threading.Tasks;

                static class Snippet
                {
                    public static void M(Task task)
                    {
                        var continuation = [|task.ContinueWith(t => { })|];
                    }
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GenericTaskReceiverContinueWithFiresAsync()
    {
        var test = new Test
        {
            TestCode = """
                using System.Threading.Tasks;

                static class Snippet
                {
                    public static void M(Task<int> task)
                    {
                        var continuation = [|task.ContinueWith(t => { })|];
                    }
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UserDefinedContinueWithDoesNotFireAsync()
    {
        var test = new Test
        {
            TestCode = """
                static class Snippet
                {
                    public static void M(Widget widget)
                    {
                        var result = widget.ContinueWith();
                    }
                }

                public sealed class Widget
                {
                    public int ContinueWith() => 0;
                }
                """,
        };

        await test.RunAsync(CancellationToken.None);
    }

    private sealed class Test : LifetimeAnalyzerTest<ContinueWithAnalyzer>;
}
