using WinForward.Windows;

namespace WinForward.Benchmarks.Stability;

internal static class SoakRunner
{
    public static async Task<int> RunAsync(SoakOptions options)
    {
        // The child server mode owns stdout — its handshake line is the parent's contract — so it
        // never enters the scenario loop, whose context writes a metadata row to stdout first.
        if (options.ServeSocks5Udp) return await Socks5UdpServerMode.RunAsync(options).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows()) return await RunScenariosAsync(options).ConfigureAwait(false);

        // 10 ms pacing ticks need the 1 ms system timer; the ~15.6 ms default starves them.
        using var timer = new HighResolutionTimerScope();
        if (!timer.IsEnabled)
        {
            await Console.Error.WriteLineAsync("Failed to raise the Windows timer resolution to 1 ms; pacing-sensitive stability numbers are degraded.").ConfigureAwait(false);
        }

        return await RunScenariosAsync(options).ConfigureAwait(false);
    }

    private static async Task<int> RunScenariosAsync(SoakOptions options)
    {
        var fileOutput = OpenOutput(options.OutputPath);
        using var context = new StabilityContext(options, fileOutput);
        var failures = 0;
        foreach (var (name, run) in SelectScenarios(options.Scenario))
        {
            try
            {
                await run(context, options).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures++;
                await Console.Error.WriteLineAsync($"Stability scenario '{name}' failed: {exception}").ConfigureAwait(false);
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static StreamWriter? OpenOutput(string? outputPath)
    {
        if (outputPath is null) return null;
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        return new StreamWriter(outputPath, append: false);
    }

    /// <summary>
    /// The scenarios a <see cref="SoakScenario"/> selection expands to. Only sustained relay load
    /// belongs in <see cref="SoakScenario.All"/>: a scenario whose own default resolves minutes
    /// long would silently drag every existing run to that length, and a controlled probe or
    /// census — contention, flow-table sweep, capture-pump wake, residency, attribution, session
    /// retention — would add a load or a topology no existing row depends on.
    /// </summary>
    internal static IReadOnlyList<(string Name, Func<StabilityContext, SoakOptions, Task> Run)> SelectScenarios(SoakScenario scenario)
    {
        return scenario switch
        {
            SoakScenario.Udp => [("udp", UdpLossScenario.RunAsync)],
            SoakScenario.Tcp => [("tcp", TcpEofScenario.RunAsync)],
            SoakScenario.TcpThroughput => [("tcpThroughput", TcpThroughputScenario.RunAsync)],
            SoakScenario.Footprint => [("footprint", SessionFootprintScenario.RunAsync)],
            SoakScenario.Baseline => [("baseline", UdpRawBaselineScenario.RunAsync)],
            SoakScenario.Burst => [("udpBurst", UdpBurstScenario.RunAsync)],
            SoakScenario.Churn => [("udpChurn", UdpChurnScenario.RunAsync)],
            SoakScenario.SessionBudget => [("udpSessionBudget", UdpSessionBudgetScenario.RunAsync)],
            SoakScenario.Scaling => [("scaling", ScalingContentionScenario.RunAsync)],
            SoakScenario.Sweep => [("flowTableSweep", SweepPauseScenario.RunAsync)],
            SoakScenario.Pump => [("pump", PumpIdleWakeScenario.RunAsync)],
            SoakScenario.TcpChurn => [("tcpChurn", TcpChurnScenario.RunAsync)],
            SoakScenario.Residency => [("residency", ResidencyCensusScenario.RunAsync)],
            SoakScenario.Attribution => [("attribution", AttributionOffPumpScenario.RunAsync)],
            SoakScenario.Retention => [("retention", UdpSessionRetentionScenario.RunAsync)],
            SoakScenario.GcSoak => [("gcSoak", GcSoakScenario.RunAsync)],
            _ =>
            [
                ("udp", UdpLossScenario.RunAsync),
                ("tcp", TcpEofScenario.RunAsync),
                ("tcpThroughput", TcpThroughputScenario.RunAsync),
                ("footprint", SessionFootprintScenario.RunAsync),
                ("baseline", UdpRawBaselineScenario.RunAsync),
                ("udpBurst", UdpBurstScenario.RunAsync),
            ],
        };
    }
}
