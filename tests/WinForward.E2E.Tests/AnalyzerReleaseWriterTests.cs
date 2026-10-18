using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The release writer end to end: a copied frozen campaign is analyzed twice, the legacy documents must
/// stay byte-identical, and the three release files must carry the decision, the gate matrix and the clock
/// evidence. The frozen tree itself is never written to.
/// </summary>
public sealed class AnalyzerReleaseWriterTests
{
    private static readonly string[] s_releaseFiles =
        ["verdict.release.json", "tables.release.md", "findings.release.json"];

    private static readonly double[] s_scatter = [-15.0, -5.0, 0.0, 5.0, 15.0];
    [Fact]
    public void AReleaseRunWritesTheReleaseFilesAndLeavesTheLegacyBytesIdentical()
    {
        using var campaign = new TemporaryCampaign();
        var legacy = campaign.Output("legacy");
        var release = campaign.Output("release");

        Assert.Equal(0, Run(campaign.Raw, legacy));
        Assert.Equal(
            0,
            Run(campaign.Raw, release, "--release", "--thresholds", Fixture("minimal.json"), "--min-passes", "2", "--discard-pass", "1"));

        foreach (var name in new[] { "tables.md", "verdict.json", "plots/SKIPPED.md" })
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(legacy, name)),
                File.ReadAllBytes(Path.Combine(release, name)));
        }

        foreach (var name in s_releaseFiles)
        {
            Assert.True(File.Exists(Path.Combine(release, name)), $"{name} was not written");
        }

        Assert.True(File.Exists(Path.Combine(release, "release-inputs.json")));
        Assert.Equal("release-inputs/1", Text(release, "release-inputs.json")["schema"]!.GetValue<string>());
        Assert.Equal("release-verdict/1", Text(release, "verdict.release.json")["schema"]!.GetValue<string>());
        Assert.Equal("release-findings/1", Text(release, "findings.release.json")["schema"]!.GetValue<string>());
        Assert.Contains("## Gate", File.ReadAllText(Path.Combine(release, "tables.release.md")), StringComparison.Ordinal);

        var verdict = Text(release, "verdict.release.json");
        Assert.Equal(
            "schema,release,caliber,passes,referenceRow,opponents,primary,exploratory,capability,clock,gates,findings,findingsBySeverity,coverage,limits",
            string.Join(",", verdict.AsObject().Select(member => member.Key)));
        Assert.Equal(2, verdict["passes"]!["n"]!.GetValue<int>());
        Assert.Equal(1, verdict["passes"]!["discarded"]![0]!.GetValue<int>());
        Assert.Equal(2, verdict["passes"]!["measured"]![0]!.GetValue<int>());
        Assert.Equal("wf-aot-nativeudp", verdict["referenceRow"]!.GetValue<string>());
        Assert.Equal(4, verdict["primary"]!.AsArray().Count);

        var latency = verdict["primary"]!.AsArray()
            .Select(metric => metric!.AsObject())
            .Single(metric => metric["metric"]!.GetValue<string>() == "lat.tcp_rtt.p50");
        Assert.Equal("ratio", latency["kind"]!.GetValue<string>());
        Assert.Equal(0.18, latency["band"]!.GetValue<double>());
        Assert.Equal(0.18, latency["mdd"]!.GetValue<double>());
        Assert.NotNull(latency["referenceMedian"]);
        Assert.NotNull(latency["median"]);
        Assert.NotNull(latency["rawCi"]);
        Assert.NotNull(latency["adjustedCi"]);
        Assert.False(string.IsNullOrEmpty(latency["decisionPair"]!.GetValue<string>()));
        Assert.Equal(3, latency["familySize"]!.GetValue<int>());
        var pair = latency["pairs"]!.AsArray()
            .Select(entry => entry!.AsObject())
            .Single(entry => entry["rowId"]!.GetValue<string>() == "proxifyre");
        Assert.NotNull(pair["rawCi"]);
        Assert.NotNull(pair["adjustedCi"]);
        Assert.False(string.IsNullOrEmpty(pair["state"]!.GetValue<string>()));
        Assert.DoesNotContain(
            verdict["gates"]!["invalidations"]!.AsArray(),
            entry => entry!["pass"]!.GetValue<string>() == "pass1");

        var loss = verdict["primary"]!.AsArray()
            .Select(metric => metric!.AsObject())
            .Single(metric => metric["metric"]!.GetValue<string>() == "loss.lossRate");
        Assert.Equal("proxybridge", loss["decisionPair"]!.GetValue<string>());
        Assert.NotNull(loss["adjustedCi"]);

        Assert.False(verdict["clock"]!["applied"]!.GetValue<bool>());
        Assert.Equal("absent", verdict["clock"]!["status"]!.GetValue<string>());
        Assert.True(verdict["clock"]!["correctedLedger"]!["arms"]!.GetValue<int>() > 0);
        Assert.True(verdict["clock"]!["uncorrectedLedger"]!["arms"]!.GetValue<int>() > 0);
        Assert.Null(verdict["coverage"]);
        Assert.Equal(8, verdict["limits"]!.AsArray().Count);
        Assert.NotEmpty(verdict["findings"]!.AsArray());
    }

    [Fact]
    public void ABoundaryTreeInvalidatesACellAndShrinksTheFamily()
    {
        using var campaign = new TemporaryCampaign();
        var raw = campaign.Copy("boundary");
        SetGate(raw, "pass2", "proxifyre", "LOSS", "clientSendLoss", 7);
        SetGate(raw, "pass3", "proxifyre", "LOSS", "clientSendLoss", 7);
        var output = campaign.Output("boundary-out");

        Assert.Equal(
            0,
            Run(raw, output, "--release", "--thresholds", Fixture("minimal.json"), "--min-passes", "2", "--discard-pass", "1"));

        var verdict = Text(output, "verdict.release.json");
        var invalidations = Invalidations(verdict);
        Assert.Contains(
            invalidations,
            entry => entry is { RowId: "proxifyre", Metric: "loss.lossRate", Gate: "clientSendLoss" });
        Assert.Contains(
            invalidations,
            entry => entry is { RowId: "proxifyre", Pass: "pass2" });

        var loss = verdict["primary"]!.AsArray()
            .Select(metric => metric!.AsObject())
            .Single(metric => metric["metric"]!.GetValue<string>() == "loss.lossRate");
        Assert.Equal(1, loss["familySize"]!.GetValue<int>());

        var pair = loss["pairs"]!.AsArray()
            .Select(entry => entry!.AsObject())
            .Single(entry => entry["rowId"]!.GetValue<string>() == "proxifyre");
        Assert.False(pair["inFamily"]!.GetValue<bool>());
        Assert.Equal("unevaluable", pair["state"]!.GetValue<string>());
        Assert.Contains("a release gate invalidated all", pair["reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(2, pair["invalidatedPasses"]!.AsArray().Count);
    }

    [Fact]
    public void TheVerdictCarriesBothClockReadsAndThePerPassEvidence()
    {
        using var campaign = new TemporaryCampaign();
        var raw = campaign.Copy("clocked");
        WriteClock(raw);
        var output = campaign.Output("clocked-out");

        Assert.Equal(
            0,
            Run(raw, output, "--release", "--thresholds", Fixture("minimal.json"), "--min-passes", "2", "--discard-pass", "1"));

        var clock = Text(output, "verdict.release.json")["clock"]!;
        Assert.Equal("complete", clock["status"]!.GetValue<string>());
        Assert.True(clock["applied"]!.GetValue<bool>());
        Assert.Equal(30, clock["samples"]!.GetValue<int>());
        Assert.Equal(0, clock["badLines"]!.GetValue<int>());
        Assert.True(clock["correctedLedger"]!["arms"]!.GetValue<int>() > 0);
        Assert.True(clock["uncorrectedLedger"]!["arms"]!.GetValue<int>() > 0);

        var pass = clock["passes"]!["pass2"]!;
        Assert.Equal("measured", pass["role"]!.GetValue<string>());
        Assert.Equal("interpolated", pass["status"]!.GetValue<string>());
        Assert.Equal(1800.0, pass["preOffsetMs"]!.GetValue<double>(), 6);
        Assert.Equal(2200.0, pass["postOffsetMs"]!.GetValue<double>(), 6);
        Assert.Equal(400.0, pass["driftMs"]!.GetValue<double>(), 6);
        Assert.Equal(2000.0, pass["offsetMs"]!.GetValue<double>(), 6);
        Assert.True(pass["residualMs"]!.GetValue<double>() > 0.0);
        Assert.True(pass["driftExceeded"]!.GetValue<bool>());

        var report = File.ReadAllText(Path.Combine(output, "tables.release.md"));
        Assert.Contains("| complete |", report, StringComparison.Ordinal);
        Assert.Contains("| pass2 | measured | interpolated |", report, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverageIsReadFromBesideTheThresholdsAndAbsentIsNull()
    {
        using var campaign = new TemporaryCampaign();
        var directory = campaign.Output("thresholds");
        var thresholds = Path.Combine(directory, "thresholds.json");
        File.Copy(Fixture("minimal.json"), thresholds);
        var coverage = Path.Combine(directory, "coverage.json");
        File.WriteAllText(coverage, "{\"n\":6,\"percentile\":0.94,\"adjusted\":0.95}\n");
        var withCoverage = campaign.Output("with-coverage");
        var withoutCoverage = campaign.Output("without-coverage");

        Assert.Equal(0, Run(campaign.Raw, withCoverage, "--release", "--thresholds", thresholds, "--min-passes", "2", "--discard-pass", "1"));
        var verdict = Text(withCoverage, "verdict.release.json");
        Assert.Equal(6, verdict["coverage"]!["n"]!.GetValue<int>());
        Assert.Equal(0.94, verdict["coverage"]!["percentile"]!.GetValue<double>(), 6);
        Assert.Contains("\"percentile\": 0.94", File.ReadAllText(Path.Combine(withCoverage, "tables.release.md")), StringComparison.Ordinal);

        File.Delete(coverage);
        Assert.Equal(0, Run(campaign.Raw, withoutCoverage, "--release", "--thresholds", thresholds, "--min-passes", "2", "--discard-pass", "1"));
        var absent = Text(withoutCoverage, "verdict.release.json");
        Assert.Null(absent["coverage"]);
        Assert.Contains("is absent", File.ReadAllText(Path.Combine(withoutCoverage, "tables.release.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedCoverageIsDisclosedAsNullRatherThanFabricated()
    {
        using var campaign = new TemporaryCampaign();
        var directory = campaign.Output("bad-coverage-thresholds");
        var thresholds = Path.Combine(directory, "thresholds.json");
        File.Copy(Fixture("minimal.json"), thresholds);
        File.WriteAllText(Path.Combine(directory, "coverage.json"), "{not json");
        var output = campaign.Output("bad-coverage-out");

        Assert.Equal(0, Run(campaign.Raw, output, "--release", "--thresholds", thresholds, "--min-passes", "2", "--discard-pass", "1"));

        var verdict = Text(output, "verdict.release.json");
        Assert.Null(verdict["coverage"]);
        Assert.Contains(
            verdict["findings"]!.AsArray(),
            finding => finding!["kind"]!.GetValue<string>() == "coverage-unreadable");
        Assert.Contains("unreadable", File.ReadAllText(Path.Combine(output, "tables.release.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void ARequiredClockWithoutALogIsRefusedBeforeAnyOutput()
    {
        using var campaign = new TemporaryCampaign();
        var directory = campaign.Output("required-thresholds");
        var thresholds = Path.Combine(directory, "thresholds.json");
        var root = JsonNode.Parse(File.ReadAllText(Fixture("minimal.json")))!.AsObject();
        root["clock"]!.AsObject()["require"] = true;
        File.WriteAllText(
            thresholds,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var output = campaign.Output("required-out");

        var (exitCode, log) = RunRaw(
            campaign.Raw,
            output,
            "--release",
            "--thresholds",
            thresholds,
            "--min-passes",
            "2",
            "--discard-pass",
            "1");

        Assert.Equal(2, exitCode);
        Assert.Contains("clock.require", log, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(output, "release-inputs.json")));
        Assert.False(File.Exists(Path.Combine(output, "tables.md")));
        Assert.False(File.Exists(Path.Combine(output, "verdict.json")));
        foreach (var name in s_releaseFiles)
        {
            Assert.False(File.Exists(Path.Combine(output, name)), $"{name} was written by a refused release run");
        }
    }

    [Fact]
    public void APartialClockIsDisclosedPassByPass()
    {
        using var campaign = new TemporaryCampaign();
        var raw = campaign.Copy("partial-clock");
        WriteClock(raw, partial: true);
        var output = campaign.Output("partial-clock-out");

        Assert.Equal(
            0,
            Run(raw, output, "--release", "--thresholds", Fixture("minimal.json"), "--min-passes", "2", "--discard-pass", "1"));

        var verdict = Text(output, "verdict.release.json");
        Assert.Equal("partial", verdict["clock"]!["status"]!.GetValue<string>());
        Assert.True(verdict["clock"]!["applied"]!.GetValue<bool>());
        var pass = verdict["clock"]!["passes"]!["pass2"]!;
        Assert.Equal("pre-only", pass["status"]!.GetValue<string>());
        Assert.Null(pass["postOffsetMs"]);
        Assert.Null(pass["driftMs"]);
        Assert.Equal(1800.0, pass["offsetMs"]!.GetValue<double>(), 6);
        Assert.Contains(
            verdict["findings"]!.AsArray(),
            finding => finding!["kind"]!.GetValue<string>() == "clock-partial");
    }

    [Fact]
    public void ALegacyRunWritesNoReleaseFiles()
    {
        using var campaign = new TemporaryCampaign();
        var output = campaign.Output("legacy-only");

        Assert.Equal(0, Run(campaign.Raw, output));
        foreach (var name in s_releaseFiles)
        {
            Assert.False(File.Exists(Path.Combine(output, name)), $"{name} was written by a legacy run");
        }

        Assert.False(File.Exists(Path.Combine(output, "release-inputs.json")));
    }

    private static string Fixture(string name) => RepoPaths.ThresholdFixture(name);

    private static List<(string? RowId, string? Metric, string? Gate, string? Pass)> Invalidations(JsonNode verdict) =>
        [.. verdict["gates"]!["invalidations"]!.AsArray().Select(entry => (
            entry?["rowId"]?.GetValue<string>(), entry?["metric"]?.GetValue<string>(),
            entry?["gate"]?.GetValue<string>(), entry?["pass"]?.GetValue<string>()))];
    private static JsonNode Text(string directory, string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(directory, name)))!;

    private static int Run(string raw, string output, params string[] extra)
    {
        var (exitCode, log) = RunRaw(raw, output, extra);
        Assert.True(exitCode == 0, $"the analysis exited {exitCode}:{Environment.NewLine}{log}");
        return exitCode;
    }

    private static (int ExitCode, string Log) RunRaw(string raw, string output, params string[] extra)
    {
        var info = new ProcessStartInfo(AnalysisBinary())
        {
            WorkingDirectory = RepoPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("--raw");
        info.ArgumentList.Add(raw);
        info.ArgumentList.Add("--out");
        info.ArgumentList.Add(output);
        foreach (var argument in extra)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process();
        process.StartInfo = info;
        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + Environment.NewLine + stderr);
    }

    private static string AnalysisBinary() => Path.Combine(
        AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "WinForward.E2E.Analysis.exe" : "WinForward.E2E.Analysis");

    private static void SetGate(string raw, string pass, string rowId, string arm, string gate, double value)
    {
        var path = Path.Combine(raw, pass, rowId, $"{arm}.jsonl");
        var lines = File.ReadAllLines(path);
        var changed = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var record = JsonNode.Parse(lines[index]);
            if (record is not JsonObject result
                || result["type"]?.GetValue<string>() != "result")
            {
                continue;
            }

            result["gates"] ??= new JsonObject();
            result["gates"]![gate] = value;
            lines[index] = result.ToJsonString();
            changed++;
        }

        Assert.True(changed > 0, $"{path} holds no result record");
        File.WriteAllLines(path, lines);
    }

    private static void WriteClock(string raw, bool partial = false)
    {
        var lines = new List<string>();
        foreach (var passDirectory in Directory.GetDirectories(raw, "pass*").OrderBy(path => path, StringComparer.Ordinal))
        {
            var pass = int.Parse(Path.GetFileName(passDirectory)[4..], System.Globalization.CultureInfo.InvariantCulture);
            var start = new DateTimeOffset(2026, 10, 6, 8, pass, 0, TimeSpan.Zero);
            for (var index = 0; index < 5; index++)
            {
                AddProbe(lines, pass, "pre", 1800.0 + s_scatter[index], start.AddMilliseconds(index * 150.0));
                if (!partial || pass != 2)
                {
                    AddProbe(lines, pass, "post", 2200.0 + s_scatter[index], start.AddHours(1).AddMilliseconds(index * 150.0));
                }
            }
        }

        File.WriteAllText(Path.Combine(raw, "clock.jsonl"), string.Join("\n", lines) + "\n");
    }

    private static void AddProbe(List<string> lines, int pass, string phase, double offset, DateTimeOffset vmUtc)
    {
        var hostUtc = vmUtc.AddMilliseconds(-offset);
        lines.Add(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{{\"pass\":{pass},\"phase\":\"{phase}\",\"offsetMs\":{offset:0.###},\"rttMs\":0.4,\"vmUtc\":\"{Stamp(vmUtc)}\",\"hostUtc\":\"{Stamp(hostUtc)}\"}}"));
    }

    private static string Stamp(DateTimeOffset stamp) =>
        stamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class TemporaryCampaign : IDisposable
    {
        private readonly string _root;

        internal TemporaryCampaign()
        {
            _root = Path.Combine(Path.GetTempPath(), $"wf-release-writer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            Extract(Path.Combine(_root, "tree"));
        }

        internal string Raw => Path.Combine(_root, "tree", "raw");

        internal string Output(string name)
        {
            var directory = Path.Combine(_root, name);
            Directory.CreateDirectory(directory);
            return directory;
        }

        internal string Copy(string name)
        {
            var destination = Path.Combine(_root, name);
            CopyTree(Path.Combine(_root, "tree"), destination);
            return Path.Combine(destination, "raw");
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);

        private static void CopyTree(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
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
    }
}
