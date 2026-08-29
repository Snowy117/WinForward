namespace WinForward.Benchmarks.Stability;

internal static class SoakRunner
{
    public static async Task<int> RunAsync(SoakOptions options)
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
            SoakScenario.Footprint => new List<(string, Func<StabilityContext, SoakOptions, Task>)> { ("footprint", SessionFootprintScenario.RunAsync) },
            _ => new List<(string, Func<StabilityContext, SoakOptions, Task>)>
            {
                ("udp", UdpLossScenario.RunAsync),
                ("tcp", TcpEofScenario.RunAsync),
                ("footprint", SessionFootprintScenario.RunAsync),
            },
        };
    }
}
