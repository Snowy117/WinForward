using WinForward.E2E.Analysis.Cli;

namespace WinForward.E2E.Analysis;

/// <summary>
/// The analysis's entry point: parse the command line, then run one analysis and return its exit
/// code. Internal rather than private so the argument walk can be replayed from a test without
/// starting a process.
/// </summary>
internal static class Program
{
    internal static int Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            PrintUsage();
            return ExitCodes.Success;
        }

        if (!AnalysisOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"analyze.py: {error}");
            Console.Error.WriteLine($"usage: {AnalysisOptions.Usage}");
            return ExitCodes.InputError;
        }

        return AnalysisRunner.Run(options);
    }

    private static void PrintUsage()
    {
        Console.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"""
            WinForward.E2E.Analysis - the end-to-end campaign analysis, as a compiled replacement for the
            reference implementation it is diffed against.

            Usage:
              {AnalysisOptions.Usage}

            The parameters and their defaults are the reference implementation's own:
            raw {AnalysisOptions.DefaultRaw}, out {AnalysisOptions.DefaultOut}, warmup {AnalysisOptions.DefaultWarmupSeconds:0.0#} s,
            {AnalysisOptions.DefaultResamples} resamples, seed {AnalysisOptions.DefaultSeed}.
            """));
    }
}
