using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinForward.E2E.Cli;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Client;

/// <summary>One arm's place in run.json: what it ran, where its records are and how it ended.</summary>
internal sealed record ArmSummary(string Name, string Kind, string File, long StartedTicks, long EndedTicks, bool Failed);

/// <summary>
/// Writes the run's <c>run.json</c>: the environment the whole run was configured with, and one
/// summary per arm in the order the arms ran.
/// </summary>
internal static class RunFileWriter
{
    internal static async ValueTask WriteRunFileAsync(
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
            writer.WriteString(ArmKeys.Run.Type, "run");
            WriteEnvironment(writer, options, targetAddress, planHash);
            writer.WriteString(ArmKeys.Run.StartedUtc, startedUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString(ArmKeys.Run.EndedUtc, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber(ArmKeys.Run.StartedTicks, startTicks);
            writer.WriteNumber(ArmKeys.Run.EndedTicks, endTicks);
            writer.WriteNumber(ArmKeys.Run.WallSeconds, NumberFormat.Round(Clock.ToSeconds(endTicks - startTicks)));
            writer.WritePropertyName(ArmKeys.Run.Arms);
            writer.WriteStartArray();
            foreach (var summary in summaries)
            {
                writer.WriteStartObject();
                writer.WriteString(ArmKeys.Run.Arm.Name, summary.Name);
                writer.WriteString(ArmKeys.Run.Arm.Kind, summary.Kind);
                writer.WriteString(ArmKeys.Run.Arm.File, summary.File);
                writer.WriteNumber(ArmKeys.Run.Arm.StartedTicks, summary.StartedTicks);
                writer.WriteNumber(ArmKeys.Run.Arm.EndedTicks, summary.EndedTicks);
                writer.WriteBoolean(ArmKeys.Run.Arm.Failed, summary.Failed);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean(ArmKeys.Run.Failed, failed);
            writer.WriteEndObject();
        }

        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static void WriteEnvironment(Utf8JsonWriter writer, ClientOptions options, IPAddress targetAddress, string planHash)
    {
        writer.WriteString(ArmKeys.Run.Label, options.Label);
        writer.WriteString(ArmKeys.Run.ClientVersion, typeof(ClientRunner).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");
        writer.WriteString(ArmKeys.Run.OsDescription, RuntimeInformation.OSDescription);
        writer.WriteString(ArmKeys.Run.FrameworkDescription, RuntimeInformation.FrameworkDescription);
        writer.WriteNumber(ArmKeys.Run.LogicalProcessors, Environment.ProcessorCount);
        writer.WriteString(ArmKeys.Run.PlanHash, planHash);
        if (options.PlanPath is null)
        {
            writer.WriteNull(ArmKeys.Run.PlanPath);
        }
        else
        {
            writer.WriteString(ArmKeys.Run.PlanPath, Path.GetFullPath(options.PlanPath));
        }

        // planPath answers "which file"; planSource answers "was there one at all", which a consumer
        // needs without treating the null as a missing value.
        writer.WriteString(ArmKeys.Run.PlanSource, options.PlanPath is null ? "builtin" : "file");

        writer.WriteString(ArmKeys.Run.OutDirectory, Path.GetFullPath(options.OutDirectory));
        writer.WritePropertyName(ArmKeys.Run.Target);
        writer.WriteStartObject();
        writer.WriteString(ArmKeys.Run.TargetObject.Address, targetAddress.ToString());
        writer.WriteNumber(ArmKeys.Run.TargetObject.TcpPort, options.TcpPort);
        writer.WriteNumber(ArmKeys.Run.TargetObject.UdpPort, options.UdpPort);
        writer.WriteNumber(ArmKeys.Run.TargetObject.DnsPort, options.DnsPort);
        writer.WriteEndObject();
        writer.WritePropertyName(ArmKeys.Run.SamplerProcesses);
        writer.WriteStartArray();
        foreach (var name in options.SamplerProcesses)
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
    }
}
