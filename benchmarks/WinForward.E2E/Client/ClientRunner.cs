using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using WinForward.E2E.Cli;
using WinForward.E2E.Client.Arms;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Client;

internal static class ClientRunner
{
    internal static async Task<int> RunAsync(ClientOptions options, CancellationToken cancellationToken)
    {
        if (!PlanFile.TryLoad(options.PlanPath, out var arms, out var planBytes, out var planError))
        {
            await Console.Error.WriteLineAsync($"e2e client: {planError}").ConfigureAwait(false);
            return ExitCodes.UsageError;
        }

        if (!IPAddress.TryParse(options.TargetAddress, out var targetAddress))
        {
            await Console.Error.WriteLineAsync($"e2e client: --target '{options.TargetAddress}' is not an IP address literal.").ConfigureAwait(false);
            return ExitCodes.UsageError;
        }

        if (!TryValidateOutputPaths(options, arms, out var pathError))
        {
            await Console.Error.WriteLineAsync($"e2e client: {pathError}").ConfigureAwait(false);
            return ExitCodes.UsageError;
        }

        Directory.CreateDirectory(options.OutDirectory);
        await using var sampler = new ResourceSampler(options.SamplerProcesses);

        var runStartTicks = Clock.Now;
        var startedUtc = DateTimeOffset.UtcNow;
        var planHash = Convert.ToHexString(SHA256.HashData(planBytes)).ToLowerInvariant()[..16];
        var summaries = new List<ArmSummary>();
        var failed = false;

        foreach (var arm in arms)
        {
            var summary = await RunArmAsync(options, arm, targetAddress, sampler, cancellationToken).ConfigureAwait(false);
            summaries.Add(summary);
            failed |= summary.Failed;
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        var runEndTicks = Clock.Now;
        await RunFileWriter.WriteRunFileAsync(options, summaries, planHash, targetAddress, runStartTicks, runEndTicks, startedUtc, failed).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"e2e client: {summaries.Count} arm(s) written to {Path.GetFullPath(options.OutDirectory)}")).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("e2e client: interrupted; the arms above are incomplete.").ConfigureAwait(false);
            return ExitCodes.RuntimeError;
        }

        return failed ? ExitCodes.RuntimeError : ExitCodes.Success;
    }

    // 255 is the length every target platform allows one path component, and 250 keeps the whole
    // output path inside the classic Windows MAX_PATH with room left for the file name. Both are
    // checked before the first file is created, so an over-long path is a usage error with a
    // message instead of a PathTooLongException out of a FileStream.
    private const int MaxPathComponentLength = 255;
    private const int MaxOutputPathLength = 250;

    private static readonly char[] s_pathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private static bool TryValidateOutputPaths(ClientOptions options, List<ArmSpec> arms, out string? error)
    {
        var directory = Path.GetFullPath(options.OutDirectory);
        if (!TryValidateOutputPath(directory, out error))
        {
            return false;
        }

        foreach (var arm in arms)
        {
            if (!TryValidateOutputPath(Path.Combine(directory, $"{PlanFile.SanitizeFileName(arm.Name)}.jsonl"), out error))
            {
                return false;
            }
        }

        return TryValidateOutputPath(Path.Combine(directory, "run.json"), out error);
    }

    private static bool TryValidateOutputPath(string path, out string? error)
    {
        var components = path.Split(s_pathSeparators);
        var longest = Array.FindIndex(components, static component => component.Length > MaxPathComponentLength);
        if (longest >= 0)
        {
            error = $"the output path '{path}' has a {components[longest].Length}-character component, above the {MaxPathComponentLength}-character limit";
            return false;
        }

        if (path.Length > MaxOutputPathLength)
        {
            error = $"the output path '{path}' is {path.Length} characters long, above the conservative {MaxOutputPathLength}-character limit";
            return false;
        }

        error = null;
        return true;
    }

    private static async Task<ArmSummary> RunArmAsync(
        ClientOptions options,
        ArmSpec arm,
        IPAddress targetAddress,
        ResourceSampler sampler,
        CancellationToken cancellationToken)
    {
        var fileName = $"{PlanFile.SanitizeFileName(arm.Name)}.jsonl";
        var path = Path.Combine(options.OutDirectory, fileName);
        await using var sink = new JsonlSink(path, JsonlPolicy.Propagate, envelope: null);
        sampler.SetTarget(sink, arm.Name);

        var latency = new LatencySet();
        var context = new ArmContext
        {
            Spec = arm,
            Options = options,
            TargetAddress = targetAddress,
            Sink = sink,
            Latency = latency,
            CancellationToken = cancellationToken,
        };

        var startedTicks = Clock.Now;
        await Console.Out.WriteLineAsync($"e2e client: arm {arm.Name} ({arm.Kind}) starting").ConfigureAwait(false);
        var (outcome, failure, cancelled) = await RunGuardedAsync(context).ConfigureAwait(false);

        var endedTicks = Clock.Now;
        // An arm that threw before it built metrics still publishes an empty metrics object, the same
        // shape a run that reached the record writer with nothing measured always had.
        failure = await WriteArmRecordsAsync(sink, sampler, options, arm, outcome ?? ArmRecordWriter.EmptyOutcome(), latency, fileName, failure, cancelled, startedTicks, endedTicks).ConfigureAwait(false);

        await Console.Out.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"e2e client: arm {arm.Name} finished in {Clock.ToSeconds(endedTicks - startedTicks):F1}s{(failure is null ? string.Empty : " (FAILED)")}")).ConfigureAwait(false);

        return new ArmSummary(arm.Name, arm.Kind, fileName, startedTicks, endedTicks, failure is not null);
    }

    /// <summary>
    /// Runs one arm and books whatever it threw as that arm's failure. A stopped run must leave the
    /// same evidence a broken arm leaves, so a cancellation is a failure too, and only its message
    /// stays the literal "cancelled" so an interrupted arm reads the same whatever await noticed the
    /// token (D14.12). Every other exception is an arm failure: letting one escape would abort the
    /// process before run.json is written and take every completed arm's summary down with it
    /// (D1/D4/D7).
    /// </summary>
    private static async Task<(ArmOutcome? Outcome, Exception? Failure, bool Cancelled)> RunGuardedAsync(ArmContext context)
    {
        try
        {
            return (await ArmDispatch.RunAsync(context).ConfigureAwait(false), null, false);
        }
        catch (OperationCanceledException exception)
        {
            return (null, exception, true);
        }
        catch (Exception exception)
        {
            return (null, exception, false);
        }
    }

    /// <summary>
    /// Writes the arm's records and releases its sink, all inside the arm's failure boundary: a
    /// record that cannot be written, or a file that cannot be drained, fails the arm here — where
    /// run.json.failed and an `error` record follow — instead of escaping RunArmAsync and taking the
    /// remaining arms and run.json with it (D1/D4/D7, D14.7). Returns the failure the arm ends with.
    /// </summary>
    private static async ValueTask<Exception?> WriteArmRecordsAsync(
        JsonlSink sink,
        ResourceSampler sampler,
        ClientOptions options,
        ArmSpec arm,
        ArmOutcome outcome,
        LatencySet latency,
        string fileName,
        Exception? failure,
        bool cancelled,
        long startedTicks,
        long endedTicks)
    {
        var failureAttempted = false;
        try
        {
            if (failure is not null)
            {
                failureAttempted = true;
                await ArmRecordWriter.WriteFailureAsync(sink, options, arm, failure, cancelled, startedTicks, endedTicks).ConfigureAwait(false);
            }

            await ArmRecordWriter.WriteResultAsync(sink, options, arm, outcome, latency, startedTicks, endedTicks).ConfigureAwait(false);
            await ArmRecordWriter.WriteArmSummaryAsync(sink, options, arm, outcome, startedTicks, endedTicks, fileName).ConfigureAwait(false);

            // A record the file never took fails this arm, and the check runs while the sink is still
            // open, so the `error` record the failure owes its reader can still be written.
            if (sink.WriteErrors > 0 && !failureAttempted)
            {
                failure = ArmRecordWriter.LostRecords(sink);
                failureAttempted = true;
                await ArmRecordWriter.WriteFailureAsync(sink, options, arm, failure, cancelled, startedTicks, endedTicks).ConfigureAwait(false);
            }

            // The sampler holds a reference to this sink and must stop writing before it closes: a
            // tick that landed on a closed sink would be counted as another lost record of this arm.
            await sampler.ClearTargetAsync().ConfigureAwait(false);

            // The release belongs to this arm's boundary too: a file that cannot be drained fails the
            // arm. That failure cannot be written into the file it is about, so it is read back from
            // the count — run.json still carries the arm's `failed` flag and the sink reports it to
            // stderr; the `await using` above remains the non-throwing backstop (D14.7).
            await sink.CompleteAsync().ConfigureAwait(false);
            failure ??= sink.WriteErrors > 0 ? ArmRecordWriter.LostRecords(sink) : null;
            return failure;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure ??= exception;
            if (!failureAttempted)
            {
                await ArmRecordWriter.TryWriteFailureAsync(sink, options, arm, failure, cancelled, startedTicks, endedTicks).ConfigureAwait(false);
            }

            return failure;
        }
    }

    internal static void PrintHelp()
    {
        Console.WriteLine("""
            Usage: WinForward.E2E client --target <ip> --out <dir> [options]

              --target <ip>             Target host address (required, IP literal)
              --out <dir>               Output directory for <arm>.jsonl and run.json (required)
              --plan <path>             Plan JSON; omit to use the built-in default arm set
              --label <name>            Free-form label copied into every result row
              --tcp-port <n>            Target TCP port (default 30010)
              --udp-port <n>            Target UDP echo port (default 30010)
              --dns-port <n>            Target DNS port, UDP and TCP (default 53)
              --sampler-process <name>  Process name to sample at 1 Hz (repeatable, no .exe suffix)
              --inject-corrupt-every <n>  Flip one payload byte of every n-th UDP datagram without
                                        recomputing its CRC: the target drops the frame as undecodable,
                                        so the client can only ever report it as path loss (never)
              --inject-rewrite-every <n>  Flip one payload byte of every n-th UDP datagram and recompute
                                        the CRC, so the target echoes it and the client books it corrupt

            The client exits 0 when every requested arm completed, 1 when an arm failed, 2 on a
            usage error.
            """);
    }
}
