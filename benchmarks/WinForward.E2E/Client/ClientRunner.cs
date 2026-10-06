using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using WinForward.E2E.Cli;
using WinForward.E2E.Client.Arms;

namespace WinForward.E2E.Client;

internal sealed class ClientOptions
{
    internal string TargetAddress { get; set; } = string.Empty;

    internal string? PlanPath { get; set; }

    internal string OutDirectory { get; set; } = string.Empty;

    internal string Label { get; set; } = string.Empty;

    internal List<string> SamplerProcesses { get; } = [];

    internal int TcpPort { get; set; } = 30010;

    internal int UdpPort { get; set; } = 30010;

    internal int DnsPort { get; set; } = 53;

    internal int InjectCorruptEvery { get; set; }

    internal int InjectRewriteEvery { get; set; }
}

internal static class ClientRunner
{
    private static readonly string[] s_knownOptions =
    [
        "--target",
        "--plan",
        "--out",
        "--label",
        "--sampler-process",
        "--tcp-port",
        "--udp-port",
        "--dns-port",
        "--inject-corrupt-every",
        "--inject-rewrite-every",
    ];

#pragma warning disable RCS1239 // Every flag may consume the next argument as its value, so the body advances the index and S127 (error) forbids a for loop here.
    internal static bool TryCreate(string[] args, out ClientOptions options, out string? error)
    {
        options = new ClientOptions();
        error = null;

        var index = 0;
        while (index < args.Length)
        {
            var argument = args[index];
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            var name = separator >= 0 ? argument[..separator] : argument;
            var inlineValue = separator >= 0 ? argument[(separator + 1)..] : null;

            if (Array.IndexOf(s_knownOptions, name) < 0)
            {
                error = $"unknown argument '{argument}'";
                return false;
            }

            if (inlineValue is null)
            {
                if (++index >= args.Length)
                {
                    error = $"missing value for '{name}'";
                    return false;
                }

                inlineValue = args[index];
            }

            if (!TryApply(options, name, inlineValue, out error))
            {
                return false;
            }

            index++;
        }

        if (options.TargetAddress.Length == 0)
        {
            error = "--target is required";
            return false;
        }

        if (!IPAddress.TryParse(options.TargetAddress, out _))
        {
            error = $"--target '{options.TargetAddress}' is not an IP address literal";
            return false;
        }

        if (options.OutDirectory.Length == 0)
        {
            error = "--out is required";
            return false;
        }

        return true;
    }
#pragma warning restore RCS1239

    private static bool TryApply(ClientOptions options, string name, string value, out string? error)
    {
        error = null;
        switch (name)
        {
            case "--target":
                options.TargetAddress = value;
                return true;
            case "--plan":
                options.PlanPath = value;
                return true;
            case "--out":
                options.OutDirectory = value;
                return true;
            case "--label":
                options.Label = value;
                return true;
            case "--sampler-process":
                options.SamplerProcesses.Add(value);
                return true;
            case "--tcp-port":
                return TryAssignPort(value, out error, static (target, port) => target.TcpPort = port, options);
            case "--udp-port":
                return TryAssignPort(value, out error, static (target, port) => target.UdpPort = port, options);
            case "--dns-port":
                return TryAssignPort(value, out error, static (target, port) => target.DnsPort = port, options);
            case "--inject-corrupt-every":
                return TryAssignCount(value, out error, static (target, count) => target.InjectCorruptEvery = count, options);
            case "--inject-rewrite-every":
                return TryAssignCount(value, out error, static (target, count) => target.InjectRewriteEvery = count, options);
            default:
                error = $"unknown argument '{name}'";
                return false;
        }
    }

    private static bool TryAssignPort(string value, out string? error, Action<ClientOptions, int> assign, ClientOptions options)
    {
        if (!TryPort(value, out var port, out error))
        {
            return false;
        }

        assign(options, port);
        return true;
    }

    private static bool TryAssignCount(string value, out string? error, Action<ClientOptions, int> assign, ClientOptions options)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0)
        {
            error = $"'{value}' is not a positive count";
            return false;
        }

        error = null;
        assign(options, count);
        return true;
    }

    private static bool TryPort(string value, out int port, out string? error)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535)
        {
            error = null;
            return true;
        }

        port = 0;
        error = $"'{value}' is not a port number in 1..65535";
        return false;
    }

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
        await WriteRunFileAsync(options, summaries, planHash, targetAddress, runStartTicks, runEndTicks, startedUtc, failed).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"e2e client: {summaries.Count} arm(s) written to {Path.GetFullPath(options.OutDirectory)}")).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("e2e client: interrupted; the arms above are incomplete.").ConfigureAwait(false);
            return ExitCodes.RuntimeError;
        }

        return failed ? ExitCodes.RuntimeError : ExitCodes.Success;
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
        await using var sink = new JsonlFile(path);
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
        ArmOutcome? outcome = null;
        string? failure = null;
        try
        {
            outcome = await ArmDispatch.RunAsync(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            failure = "cancelled";
        }
        catch (SocketException exception)
        {
            failure = exception.Message;
        }
        catch (InvalidOperationException exception)
        {
            failure = exception.Message;
        }
        catch (IOException exception)
        {
            failure = exception.Message;
        }

        var endedTicks = Clock.Now;
        var final = outcome ?? new ArmOutcome();
        if (failure is not null)
        {
            await WriteFailureAsync(sink, arm, failure, startedTicks, endedTicks).ConfigureAwait(false);
        }

        await WriteResultAsync(sink, options, arm, final, latency, startedTicks, endedTicks).ConfigureAwait(false);
        await WriteArmSummaryAsync(sink, options, arm, final, startedTicks, endedTicks, fileName).ConfigureAwait(false);
        await sink.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        await sampler.ClearTargetAsync().ConfigureAwait(false);

        await Console.Out.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"e2e client: arm {arm.Name} finished in {Clock.ToSeconds(endedTicks - startedTicks):F1}s{(failure is null ? string.Empty : " (FAILED)")}")).ConfigureAwait(false);

        return new ArmSummary(arm.Name, arm.Kind, fileName, startedTicks, endedTicks, failure is not null);
    }

    private static async ValueTask WriteFailureAsync(JsonlFile sink, ArmSpec arm, string failure, long startedTicks, long endedTicks)
    {
        await sink.WriteAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "error");
                writer.WriteString("arm", arm.Name);
                writer.WriteString("kind", arm.Kind);
                writer.WriteString("message", failure);
                writer.WriteNumber("startedTicks", startedTicks);
                writer.WriteNumber("endedTicks", endedTicks);
                writer.WriteEndObject();
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async ValueTask WriteResultAsync(
        JsonlFile sink,
        ClientOptions options,
        ArmSpec arm,
        ArmOutcome outcome,
        LatencySet latency,
        long startedTicks,
        long endedTicks)
    {
        await sink.WriteAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "result");
                writer.WriteString("arm", arm.Name);
                writer.WriteString("kind", arm.Kind);
                writer.WriteString("label", options.Label);
                writer.WritePropertyName("parameters");
                writer.WriteStartObject();
                JsonValue.WriteProperties(writer, outcome.Parameters);
                writer.WriteEndObject();
                writer.WritePropertyName("metrics");
                writer.WriteStartObject();
                JsonValue.WriteProperties(writer, outcome.Metrics);
                writer.WriteEndObject();
                latency.WriteTo(writer);
                writer.WritePropertyName("gates");
                writer.WriteStartObject();
                JsonValue.WriteProperties(writer, outcome.Gates);
                writer.WriteEndObject();
                writer.WritePropertyName("notes");
                writer.WriteStartArray();
                foreach (var note in outcome.Notes)
                {
                    writer.WriteStringValue(note);
                }

                writer.WriteEndArray();
                writer.WriteNumber("startedTicks", startedTicks);
                writer.WriteNumber("endedTicks", endedTicks);
                writer.WriteEndObject();
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async ValueTask WriteArmSummaryAsync(
        JsonlFile sink,
        ClientOptions options,
        ArmSpec arm,
        ArmOutcome outcome,
        long startedTicks,
        long endedTicks,
        string fileName)
    {
        await sink.WriteAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "armSummary");
                writer.WriteString("arm", arm.Name);
                writer.WriteString("kind", arm.Kind);
                writer.WriteString("label", options.Label);
                writer.WritePropertyName("parameters");
                writer.WriteStartObject();
                JsonValue.WriteProperties(writer, outcome.Parameters);
                writer.WriteEndObject();
                writer.WritePropertyName("gates");
                writer.WriteStartObject();
                JsonValue.WriteProperties(writer, outcome.Gates);
                writer.WriteEndObject();
                writer.WriteString("resultFile", fileName);
                writer.WriteNumber("startedTicks", startedTicks);
                writer.WriteNumber("endedTicks", endedTicks);
                writer.WriteEndObject();
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async ValueTask WriteRunFileAsync(
        ClientOptions options,
        List<ArmSummary> summaries,
        string planHash,
        IPAddress targetAddress,
        long startTicks,
        long endTicks,
        DateTimeOffset startedUtc,
        bool failed)
    {
        var path = Path.Combine(options.OutDirectory, "run.json");
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 8192, FileOptions.None);
        await using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "run");
            WriteEnvironment(writer, options, targetAddress, planHash);
            writer.WriteString("startedUtc", startedUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("endedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("startedTicks", startTicks);
            writer.WriteNumber("endedTicks", endTicks);
            writer.WriteNumber("wallSeconds", JsonValue.Round(Clock.ToSeconds(endTicks - startTicks)));
            writer.WritePropertyName("arms");
            writer.WriteStartArray();
            foreach (var summary in summaries)
            {
                writer.WriteStartObject();
                writer.WriteString("name", summary.Name);
                writer.WriteString("kind", summary.Kind);
                writer.WriteString("file", summary.File);
                writer.WriteNumber("startedTicks", summary.StartedTicks);
                writer.WriteNumber("endedTicks", summary.EndedTicks);
                writer.WriteBoolean("failed", summary.Failed);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("failed", failed);
            writer.WriteEndObject();
        }

        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static void WriteEnvironment(Utf8JsonWriter writer, ClientOptions options, IPAddress targetAddress, string planHash)
    {
        writer.WriteString("label", options.Label);
        writer.WriteString("clientVersion", typeof(ClientRunner).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");
        writer.WriteString("osDescription", RuntimeInformation.OSDescription);
        writer.WriteString("frameworkDescription", RuntimeInformation.FrameworkDescription);
        writer.WriteNumber("logicalProcessors", Environment.ProcessorCount);
        writer.WriteString("planHash", planHash);
        if (options.PlanPath is null)
        {
            writer.WriteNull("planPath");
        }
        else
        {
            writer.WriteString("planPath", Path.GetFullPath(options.PlanPath));
        }

        writer.WriteString("outDirectory", Path.GetFullPath(options.OutDirectory));
        writer.WritePropertyName("target");
        writer.WriteStartObject();
        writer.WriteString("address", targetAddress.ToString());
        writer.WriteNumber("tcpPort", options.TcpPort);
        writer.WriteNumber("udpPort", options.UdpPort);
        writer.WriteNumber("dnsPort", options.DnsPort);
        writer.WriteEndObject();
        writer.WritePropertyName("samplerProcesses");
        writer.WriteStartArray();
        foreach (var name in options.SamplerProcesses)
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
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

    private sealed record ArmSummary(string Name, string Kind, string File, long StartedTicks, long EndedTicks, bool Failed);
}
