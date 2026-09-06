using WinForward.Windows;

namespace WinForward.Benchmarks.Stability;

internal static class SoakRunner
{
    public static async Task<int> RunAsync(SoakOptions options)
    {
        if (OperatingSystem.IsWindows())
        {
            // 10 ms pacing ticks need the 1 ms system timer; the ~15.6 ms default starves them.
            using var timer = new HighResolutionTimerScope();
            if (!timer.IsEnabled)
            {
                await Console.Error.WriteLineAsync("Failed to raise the Windows timer resolution to 1 ms; pacing-sensitive stability numbers are degraded.").ConfigureAwait(false);
            }

            return await RunScenariosAsync(options).ConfigureAwait(false);
        }

        return await RunScenariosAsync(options).ConfigureAwait(false);
    }

    private static async Task<int> RunScenariosAsync(SoakOptions options)
    {
        var fileOutput = OpenOutput(options.OutputPath);
        using var context = new StabilityContext(options, fileOutput);
        var failures = 0;
        foreach (var scenario in SelectScenarios(options.Scenario))
        {
            try
            {
                await scenario.Run(context, options).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures++;
                await Console.Error.WriteLineAsync($"Stability scenario '{scenario.Name}' failed: {exception}").ConfigureAwait(false);
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static TextWriter? OpenOutput(string? outputPath)
    {
        if (outputPath is null) return null;
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        return new StreamWriter(outputPath, append: false);
    }

    private static IReadOnlyList<(string Name, Func<StabilityContext, SoakOptions, Task> Run)> SelectScenarios(SoakScenario scenario)
    {
        return scenario switch
        {
            SoakScenario.Udp => new List<(string, Func<StabilityContext, SoakOptions, Task>)> { ("udp", UdpLossScenario.RunAsync) },
            SoakScenario.Tcp => new List<(string, Func<StabilityContext, SoakOptions, Task>)> { ("tcp", TcpEofScenario.RunAsync) },
            SoakScenario.TcpThroughput => new List<(string, Func<StabilityContext, SoakOptions, Task>)> { ("tcpThroughput", TcpThroughputScenario.RunAsync) },
            SoakScenario.Footprint => new List<(string, Func<StabilityContext, SoakOptions, Task>)> { ("footprint", SessionFootprintScenario.RunAsync) },
            SoakScenario.Baseline => new List<(string, Func<StabilityContext, SoakOptions, Task>)> { ("baseline", UdpRawBaselineScenario.RunAsync) },
            SoakScenario.Burst => new List<(string, Func<StabilityContext, SoakOptions, Task>)> { ("udpBurst", UdpBurstScenario.RunAsync) },
            _ => new List<(string, Func<StabilityContext, SoakOptions, Task>)>
            {
                ("udp", UdpLossScenario.RunAsync),
                ("tcp", TcpEofScenario.RunAsync),
                ("tcpThroughput", TcpThroughputScenario.RunAsync),
                ("footprint", SessionFootprintScenario.RunAsync),
                ("baseline", UdpRawBaselineScenario.RunAsync),
                ("udpBurst", UdpBurstScenario.RunAsync),
            },
        };
    }
}
