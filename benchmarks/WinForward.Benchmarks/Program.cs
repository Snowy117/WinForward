using BenchmarkDotNet.Running;

namespace WinForward.Benchmarks;

internal static class Program
{
    public static Task<int> Main(string[] args) =>
        args is ["--stability", .. var rest]
            ? Stability.SoakRunner.RunAsync(Stability.SoakOptions.Parse(rest))
            : Task.FromResult(RunPerf(args));

    private static int RunPerf(string[] args)
    {
        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return summaries.Any(summary => !summary.ValidationErrors.IsEmpty) ? 1 : 0;
    }
}
