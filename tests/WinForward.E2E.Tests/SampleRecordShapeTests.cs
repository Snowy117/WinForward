using System.Text;
using System.Text.Json;
using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The resource sampler's two records are part of the contract: a <c>sample</c> record has three
/// shapes -- the sampler's own process, a named process that matched, and a named process that
/// matched nothing -- and <c>samplerError</c> has one. Every key of all four is declared by
/// <see cref="ArmKeys.Sample"/> (with <c>type</c> and <c>arm</c> from the shared record root), and
/// the records here are published through the production writer, so the shapes compared are the
/// shapes an arm's file receives.
/// </summary>
public sealed class SampleRecordShapeTests
{
    private const string Arm = "LAT";

    private static readonly TimeSpan s_noFlush = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task TheSelfSamplePublishesExactlyTheDeclaredKeys()
    {
        var observations = await PublishAsync((writer, target) =>
        {
            ResourceSampleWriter.WriteSampleHeader(writer, target, "self", self: true, ticks: 1, matched: 1);
            ResourceSampleWriter.WriteCounters(writer, new ProcessCounters(1.5, 2, 3, 4, 5, 6));
            writer.WriteNumber(ArmKeys.Sample.Handles, 6);

            // One process whose counters were read and one whose were not: the second is what makes
            // readErrors and readError part of this shape instead of a branch nothing publishes.
            ResourceSampleWriter.WriteProcesses(
                writer,
                [
                    new SampledProcess(7, DateTime.UnixEpoch, countersRead: true, cpuSeconds: 1.5, privateBytes: 2),
                    new SampledProcess(8, null, countersRead: false, cpuSeconds: 0, privateBytes: 0),
                ]);

            writer.WriteNumber(ArmKeys.Sample.GeneratorCpuSeconds, 1.5);
            writer.WriteNumber(ArmKeys.Sample.EnvWorkingSetBytes, 9);
        });

        Assert.Equal(
            [
                ArmKeys.Common.Record.Type,
                ArmKeys.Sample.Ticks,
                ArmKeys.Common.Record.Arm,
                ArmKeys.Sample.Process,
                ArmKeys.Sample.Self,
                ArmKeys.Sample.Matched,
                ArmKeys.Sample.Counters.CpuSeconds,
                ArmKeys.Sample.Counters.PrivateBytes,
                ArmKeys.Sample.Counters.WorkingSetBytes,
                ArmKeys.Sample.Counters.PeakWorkingSetBytes,
                ArmKeys.Sample.Counters.Threads,
                ArmKeys.Sample.Handles,
                ArmKeys.Sample.Processes,
                ArmKeys.Sample.ReadErrors,
                ArmKeys.Sample.ReadError,
                ArmKeys.Sample.GeneratorCpuSeconds,
                ArmKeys.Sample.EnvWorkingSetBytes,
            ],
            JsonPaths.TopLevel(observations));

        Assert.Equal(DeclaredProcessEntry(), JsonPaths.Under(observations, ArmKeys.Sample.Processes));
    }

    [Fact]
    public async Task TheMatchedNamedSamplePublishesExactlyTheDeclaredKeys()
    {
        var observations = await PublishAsync((writer, target) =>
        {
            ResourceSampleWriter.WriteSampleHeader(writer, target, "dotnet", self: false, ticks: 2, matched: 1);
            ResourceSampleWriter.WriteCounters(writer, new ProcessCounters(1.5, 2, 3, 4, 5, 6));
            writer.WriteNumber(ArmKeys.Sample.Handles, 6);
            ResourceSampleWriter.WriteProcesses(
                writer,
                [new SampledProcess(7, DateTime.UnixEpoch, countersRead: true, cpuSeconds: 1.5, privateBytes: 2)]);
        });

        Assert.Equal(
            [
                ArmKeys.Common.Record.Type,
                ArmKeys.Sample.Ticks,
                ArmKeys.Common.Record.Arm,
                ArmKeys.Sample.Process,
                ArmKeys.Sample.Self,
                ArmKeys.Sample.Matched,
                ArmKeys.Sample.Counters.CpuSeconds,
                ArmKeys.Sample.Counters.PrivateBytes,
                ArmKeys.Sample.Counters.WorkingSetBytes,
                ArmKeys.Sample.Counters.PeakWorkingSetBytes,
                ArmKeys.Sample.Counters.Threads,
                ArmKeys.Sample.Handles,
                ArmKeys.Sample.Processes,
                ArmKeys.Sample.ReadErrors,
            ],
            JsonPaths.TopLevel(observations));

        Assert.Equal(DeclaredProcessEntry(), JsonPaths.Under(observations, ArmKeys.Sample.Processes));
    }

    [Fact]
    public async Task TheAbsentNamedSamplePublishesTheReducedDeclaredKeys()
    {
        var observations = await PublishAsync((writer, target) =>
        {
            ResourceSampleWriter.WriteSampleHeader(writer, target, "absent-process", self: false, ticks: 3, matched: 0);
            writer.WriteBoolean(ArmKeys.Sample.Absent, value: true);
            ResourceSampleWriter.WriteProcesses(writer, []);
        });

        Assert.Equal(
            [
                ArmKeys.Common.Record.Type,
                ArmKeys.Sample.Ticks,
                ArmKeys.Common.Record.Arm,
                ArmKeys.Sample.Process,
                ArmKeys.Sample.Self,
                ArmKeys.Sample.Matched,
                ArmKeys.Sample.Absent,
                ArmKeys.Sample.Processes,
                ArmKeys.Sample.ReadErrors,
            ],
            JsonPaths.TopLevel(observations));
    }

    [Fact]
    public async Task TheSamplerErrorRecordPublishesExactlyTheDeclaredKeys()
    {
        using var stream = new MemoryStream();
        await using var sink = new JsonlSink(stream, JsonlPolicy.Propagate, envelope: null, s_noFlush);
        var target = new SamplerTarget(sink, Arm);
        await ResourceSampleWriter
            .ReportSamplingFailureAsync(target, "self", new InvalidOperationException("boom"), CancellationToken.None)
            .ConfigureAwait(false);
        await sink.CompleteAsync().ConfigureAwait(false);

        var observations = JsonPaths.FlattenJsonl(Encoding.UTF8.GetString(stream.ToArray()));

        Assert.Equal(
            [
                ArmKeys.Common.Record.Type,
                ArmKeys.Sample.Ticks,
                ArmKeys.Common.Record.Arm,
                ArmKeys.Sample.Process,
                ArmKeys.Sample.SamplerError.Error,
                ArmKeys.Sample.SamplerError.Message,
            ],
            JsonPaths.TopLevel(observations));
    }

    /// <summary>The keys one entry of the census carries, in the order the writer emits them.</summary>
    private static List<string> DeclaredProcessEntry() =>
        DeclaredKeys.Under(typeof(ArmKeys.Sample.ProcessEntry), ArmKeys.Sample.Processes);

    private static async Task<Dictionary<string, List<JsonPathObservation>>> PublishAsync(Action<Utf8JsonWriter, SamplerTarget> write)
    {
        using var stream = new MemoryStream();
        await using var sink = new JsonlSink(stream, JsonlPolicy.Propagate, envelope: null, s_noFlush);
        var target = new SamplerTarget(sink, Arm);
        await sink.WriteAsync(writer => write(writer, target), CancellationToken.None).ConfigureAwait(false);
        await sink.CompleteAsync().ConfigureAwait(false);
        return JsonPaths.FlattenJsonl(Encoding.UTF8.GetString(stream.ToArray()));
    }
}
