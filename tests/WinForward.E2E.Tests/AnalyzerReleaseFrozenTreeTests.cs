using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json.Nodes;
using WinForward.E2E.Analysis.Release;
using Xunit;
using static WinForward.E2E.Tests.ReleaseComparisonTestData;

namespace WinForward.E2E.Tests;

/// <summary>
/// The release data seam against the frozen campaign: the legacy loader and metric catalogue feed the
/// comparison, the UDP-incapable product is excluded from the loss family, the internal WinForward row
/// is report-only, the measured subset honours the discarded pass, and the REL capability gate reads the
/// arm's exposed attempt count.
/// </summary>
public sealed class AnalyzerReleaseFrozenTreeTests
{
    private const string Reference = "wf-aot-nativeudp";

    private static readonly string[] s_passes = ["pass1", "pass2", "pass3"];

    [Fact]
    public void TheFrozenTreeFeedsTheLossFamilyAndTheCapabilityGate()
    {
        using var campaign = new TemporaryCampaign();
        var thresholds = ParseFixture("minimal.json");
        var inputs = Inputs(thresholds, s_passes, required: 2);

        var read = ReleaseDataset.TryRead(campaign.Raw, inputs, resamples: 2000, seed: 20261006, out var dataset, out var error);
        Assert.True(read, error);
        Assert.NotNull(dataset);
        Assert.Equal("pass1,pass2,pass3", string.Join(",", dataset.MeasuredPasses));
        Assert.Equal(9, dataset.Rows.Count);
        Assert.Equal(21, dataset.Rows.Single(row => row.RowId == Reference).Metrics.Count);

        var report = ReleaseComparison.Evaluate(thresholds, inputs, dataset);
        var loss = report.Metrics.Single(metric => metric.Metric == "loss.lossRate");
        var lossProxifier = loss.Pairs.Single(pair => pair.RowId == "proxifier");
        var lossInternal = loss.Pairs.Single(pair => pair.RowId == "wf-aot-opt");

        Assert.Equal(2, loss.FamilySize);
        Assert.False(lossProxifier.InFamily);
        Assert.Contains("not carried", lossProxifier.Reason, StringComparison.Ordinal);
        Assert.False(lossInternal.InFamily);
        Assert.Contains("not a declared opponent", lossInternal.Ineligibility, StringComparison.Ordinal);

        var latency = report.Metrics.Single(metric => metric.Metric == "lat.tcp_rtt.p50");
        var cpu = report.Metrics.Single(metric => metric.Metric == "cpu.proxy.vcpuPct");
        var memory = report.Metrics.Single(metric => metric.Metric == "mem.privateBytes.p50");
        Assert.Equal(3, latency.FamilySize);
        Assert.Equal(3, cpu.FamilySize);
        Assert.Equal(3, memory.FamilySize);

        var capability = report.Capabilities.Single(result =>
            result.Metric == "rel.unexpectedEofRate"
            && string.Equals(result.RowId, Reference, StringComparison.Ordinal));
        Assert.Equal(450L, capability.N!.Value);
        Assert.Equal(0L, capability.Events!.Value);
        Assert.Equal(ReleaseCapabilityStatus.Fail, capability.Status);
    }

    [Fact]
    public void ANonZeroDiscardPassLeavesOnlyTheMeasuredSubset()
    {
        using var campaign = new TemporaryCampaign();
        var thresholds = ParseFixture("minimal.json");
        var inputs = Inputs(thresholds, s_passes, required: 2, discardPass: 1);

        var read = ReleaseDataset.TryRead(campaign.Raw, inputs, resamples: 2000, seed: 20261006, out var dataset, out var error);
        Assert.True(read, error);
        Assert.NotNull(dataset);
        Assert.Equal("pass2,pass3", string.Join(",", dataset.MeasuredPasses));
        Assert.Equal(2, dataset.Rows.Single(row => row.RowId == Reference).Metrics["loss.lossRate"].Values.Count);

        var report = ReleaseComparison.Evaluate(thresholds, inputs, dataset);
        Assert.Equal(2, report.N);
        Assert.Equal(2, report.Required);
        Assert.False(report.Insufficient);
        Assert.Equal("pass2,pass3", string.Join(",", report.MeasuredPasses));
    }

    [Fact]
    public void AJsonNullRateFromTheTreeIsUnevaluableAsAMissingDenominator()
    {
        using var campaign = new TemporaryCampaign();
        var thresholds = ParseFixture("minimal.json");
        var inputs = Inputs(thresholds, s_passes, required: 2);
        NullOutLossRate(campaign.Raw, "proxifyre");

        var read = ReleaseDataset.TryRead(campaign.Raw, inputs, resamples: 2000, seed: 20261006, out var dataset, out var error);
        Assert.True(read, error);
        Assert.NotNull(dataset);
        var readings = dataset.Rows.Single(row => row.RowId == "proxifyre").Metrics["loss.lossRate"];
        Assert.Empty(readings.Values);
        Assert.Equal(3, readings.MissingPasses);
        Assert.Equal(3, readings.Reasons.Count);
        Assert.All(readings.Reasons.Values, reason => Assert.Contains("null rate", reason, StringComparison.Ordinal));

        var report = ReleaseComparison.Evaluate(thresholds, inputs, dataset);
        var pair = report.Metrics.Single(metric => metric.Metric == "loss.lossRate")
            .Pairs.Single(entry => entry.RowId == "proxifyre");
        Assert.True(pair.InFamily);
        Assert.Equal(ReleaseState.Unevaluable, pair.State);
        Assert.Contains("missing denominator", pair.Reason, StringComparison.Ordinal);
    }

    /// <summary>Writes JSON null into every measured pass's LOSS <c>metrics.lossRate</c> result field.</summary>
    private static void NullOutLossRate(string raw, string rowId)
    {
        foreach (var pass in s_passes)
        {
            var path = Path.Combine(raw, pass, rowId, "LOSS.jsonl");
            var lines = File.ReadAllLines(path);
            var found = false;
            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].Contains("\"type\":\"result\"", StringComparison.Ordinal))
                {
                    continue;
                }

                var result = JsonNode.Parse(lines[index])!.AsObject();
                result["metrics"]!["lossRate"] = null;
                lines[index] = result.ToJsonString();
                found = true;
            }

            Assert.True(found, $"no result record in {path}");
            File.WriteAllLines(path, lines);
        }
    }

    private static void Extract(string destination)
    {
        using var archive = File.OpenRead(RepoPaths.FrozenSyntheticTree);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            if (string.IsNullOrEmpty(entry.Name) || string.Equals(entry.Name, "./", StringComparison.Ordinal))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destination, entry.Name));
            Assert.StartsWith(Path.GetFullPath(destination), target, StringComparison.Ordinal);
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (entry.DataStream is null)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = File.Create(target);
            entry.DataStream.CopyTo(output);
        }
    }

    private sealed class TemporaryCampaign : IDisposable
    {
        internal TemporaryCampaign()
        {
            Root = Path.Combine(Path.GetTempPath(), $"wf-release-tree-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Extract(Root);
        }

        internal string Raw => Path.Combine(Root, "raw");

        private string Root { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
