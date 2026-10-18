using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinForward.E2E.Analysis.Release;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The clock correction as the release path applies it: a copy of the clean frozen campaign is shifted by
/// two seconds on the client's clock, a <c>clock.jsonl</c> describes the offset, and the corrected ledger
/// attribution has to reproduce the unshifted tree's counts. The uncorrected read, an omitted clock log and
/// an inverted sign are the controls that must not.
/// </summary>
public sealed class AnalyzerClockCopiedTreeTests
{
    private const int ShiftMilliseconds = 2000;

    private const double PreOffsetMs = 1800.0;

    private const double PostOffsetMs = 2200.0;

    private static readonly double[] s_scatter = [-15.0, -5.0, 0.0, 5.0, 15.0];

    [Fact]
    public void TheCorrectedAttributionReproducesTheUnshiftedTreesCounts()
    {
        using var campaign = new Campaign();

        var baseline = ReadArms(campaign.BaselineRaw, clock: null);
        var uncorrected = ReadArms(campaign.ShiftedRaw, clock: null);
        var corrected = ReadArms(campaign.ShiftedRaw, campaign.Clock);

        // 288 arm windows, not 264: a pass-level dual directory is read by more than one owner, so a
        // (pass, row, arm) key collapses rows that belong to different owners and silently drops them.
        Assert.Equal(288, baseline.Count);
        Assert.Equal(ClockStatus.Complete, campaign.Clock.Status);
        Assert.Equal(3, campaign.Clock.Passes.Count);
        Assert.Equal(3, campaign.Clock.Passes.Count(pass => pass.Kind == ClockPhaseKind.Interpolated));
        Assert.Equal(ShiftMilliseconds, campaign.Clock.Passes[0].OffsetMs!.Value, 6);

        Assert.Equal(baseline, corrected);
        Assert.Equal(288, corrected.Count);
        Assert.Equal(264, Differing(baseline, uncorrected));
    }

    [Fact]
    public void AnOmittedOrInvertedClockDoesNotReproduceTheUnshiftedTreesCounts()
    {
        using var campaign = new Campaign();
        var baseline = ReadArms(campaign.BaselineRaw, clock: null);
        var uncorrected = ReadArms(campaign.ShiftedRaw, clock: null);

        Assert.Equal(288, baseline.Count);

        var omittedRaw = campaign.CopyShifted("omitted");
        File.Delete(Path.Combine(omittedRaw, ClockCorrection.FileName));
        var omittedClock = ClockCorrection.Read(omittedRaw);
        Assert.Equal(ClockStatus.Absent, omittedClock.Status);
        Assert.Equal(uncorrected, ReadArms(omittedRaw, clock: null));
        Assert.Equal(uncorrected, ReadArms(omittedRaw, omittedClock));
        Assert.Equal(264, Differing(baseline, ReadArms(omittedRaw, omittedClock)));

        var incompleteRaw = campaign.CopyShifted("incomplete");
        File.WriteAllText(
            Path.Combine(incompleteRaw, ClockCorrection.FileName),
            "not json\n{\"pass\":1,\"phase\":\"pre\"}\n");
        var incompleteClock = ClockCorrection.Read(incompleteRaw);
        Assert.Equal(ClockStatus.Incomplete, incompleteClock.Status);
        Assert.False(incompleteClock.Corrected);
        var incomplete = ReadAttribution(incompleteRaw, incompleteClock);
        Assert.False(incomplete.Corrected);
        Assert.Equal(ClockStatus.Incomplete, incomplete.Clock);
        Assert.Equal(uncorrected, Counts(incomplete));
        Assert.Equal(264, Differing(baseline, Counts(incomplete)));

        var invertedRaw = campaign.CopyShifted("inverted");
        WriteClock(invertedRaw, sign: -1);
        var invertedClock = ClockCorrection.Read(invertedRaw);
        Assert.Equal(ClockStatus.Complete, invertedClock.Status);
        Assert.Equal(-ShiftMilliseconds, invertedClock.Passes[0].OffsetMs!.Value, 6);
        Assert.Equal(264, Differing(baseline, ReadArms(invertedRaw, invertedClock)));
    }

    [Fact]
    public void TheLedgerAttributionExposesTheEvidenceTheReleaseVerdictNeeds()
    {
        using var campaign = new Campaign();

        var attribution = ReadAttribution(campaign.BaselineRaw, clock: null);

        Assert.True(attribution.Available);
        Assert.Equal(2, attribution.LedgerFiles);
        Assert.Equal("pass1,pass2,pass3", string.Join(",", attribution.Passes));
        Assert.Equal(3, attribution.Attribution.Count);
        Assert.Equal(288, attribution.Arms.Count);
        Assert.DoesNotContain(attribution.Arms, arm => arm.Unattributable);
        Assert.Contains(attribution.Arms, arm => arm.OverlappingRuns.Count > 0);
        Assert.Contains(attribution.Arms, arm => arm.ClientConnections is > 0);
        Assert.Contains(attribution.Arms, arm => arm.ClientDatagrams is > 0);
        Assert.All(attribution.Arms, arm => Assert.True(arm.DurationSeconds > 0));
    }

    private static SortedDictionary<string, LedgerCount> ReadArms(string raw, ClockCorrection? clock) =>
        Counts(ReadAttribution(raw, clock));

    private static ReleaseLedgerAttribution ReadAttribution(string raw, ClockCorrection? clock)
    {
        var read = ReleaseLedger.TryRead(raw, clock, out var attribution, out var error);

        Assert.True(read, error);
        Assert.NotNull(attribution);
        return attribution;
    }

    private static SortedDictionary<string, LedgerCount> Counts(ReleaseLedgerAttribution attribution)
    {
        var counts = new SortedDictionary<string, LedgerCount>(StringComparer.Ordinal);
        foreach (var arm in attribution.Arms)
        {
            counts[$"{arm.Pass}/{arm.Row}/{arm.Owner}/{arm.Arm}"] = new LedgerCount(arm.TcpConnections, arm.UdpDatagrams);
        }

        return counts;
    }

    private static int Differing(
        SortedDictionary<string, LedgerCount> baseline,
        SortedDictionary<string, LedgerCount> other)
    {
        Assert.Equal(baseline.Count, other.Count);
        return baseline.Count(entry => !other.TryGetValue(entry.Key, out var value) || value != entry.Value);
    }
    private static void WriteClock(string rawDirectory, int sign)
    {
        var lines = new List<string>();
        foreach (var passDirectory in PassDirectories(rawDirectory))
        {
            var (start, end) = PassBounds(passDirectory);
            var pass = int.Parse(Path.GetFileName(passDirectory)[4..], CultureInfo.InvariantCulture);
            for (var index = 0; index < s_scatter.Length; index++)
            {
                AddProbe(lines, pass, "pre", sign, PreOffsetMs, index, start.AddSeconds(-1).AddMilliseconds(index * 150.0));
                AddProbe(lines, pass, "post", sign, PostOffsetMs, index, end.AddSeconds(1).AddMilliseconds(index * 150.0));
            }
        }

        File.WriteAllText(Path.Combine(rawDirectory, ClockCorrection.FileName), string.Join("\n", lines) + "\n");
    }

    private static void AddProbe(
        List<string> lines,
        int pass,
        string phase,
        int sign,
        double centre,
        int index,
        DateTimeOffset vmUtc)
    {
        var offset = sign * (centre + s_scatter[index]);
        var hostUtc = vmUtc.AddMilliseconds(-offset);
        lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"pass\":{pass},\"phase\":\"{phase}\",\"offsetMs\":{offset:0.###},\"rttMs\":0.4,\"vmUtc\":\"{Iso(vmUtc)}\",\"hostUtc\":\"{Iso(hostUtc)}\"}}"));
    }

    private static IEnumerable<string> PassDirectories(string rawDirectory) =>
        Directory.GetDirectories(rawDirectory, "pass*")
            .OrderBy(path => int.Parse(Path.GetFileName(path)[4..], CultureInfo.InvariantCulture));

    private static (DateTimeOffset Start, DateTimeOffset End) PassBounds(string passDirectory)
    {
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;
        foreach (var path in Directory.EnumerateFiles(passDirectory, "run.json", SearchOption.AllDirectories))
        {
            var document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            Assert.NotNull(document);
            var stamp = DateTimeOffset.Parse(
                document["startedUtc"]!.GetValue<string>(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            var wallSeconds = double.Parse(document["wallSeconds"]!.ToJsonString(), CultureInfo.InvariantCulture);
            start = start is null || stamp < start ? stamp : start;
            var finish = stamp.AddSeconds(wallSeconds);
            end = end is null || finish > end ? finish : end;
        }

        Assert.NotNull(start);
        Assert.NotNull(end);
        return (start.Value, end.Value);
    }

    private static void ShiftRunStarts(string rawDirectory, TimeSpan shift)
    {
        var shifted = 0;
        foreach (var path in Directory.EnumerateFiles(rawDirectory, "run.json", SearchOption.AllDirectories))
        {
            var document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            Assert.NotNull(document);
            var stamp = DateTimeOffset.Parse(
                document["startedUtc"]!.GetValue<string>(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            document["startedUtc"] = Iso(stamp.Add(shift));
            File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            shifted++;
        }

        Assert.True(shifted > 0, "the copied tree holds no run.json");
    }

    private static string Iso(DateTimeOffset stamp) =>
        stamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);

    private static void Extract(string destination)
    {
        Directory.CreateDirectory(destination);
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

    private static void Copy(string source, string destination)
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

    private readonly record struct LedgerCount(int TcpConnections, double UdpDatagrams);

    /// <summary>One extracted, shifted and clocked copy of the frozen campaign, deleted when the test ends.</summary>
    private sealed class Campaign : IDisposable
    {
        private readonly string _root;

        internal Campaign()
        {
            _root = Path.Combine(Path.GetTempPath(), $"wf-clock-tree-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            Extract(Path.Combine(_root, "baseline"));
            Copy(Path.Combine(_root, "baseline"), Path.Combine(_root, "shifted"));
            ShiftRunStarts(Path.Combine(_root, "shifted", "raw"), TimeSpan.FromMilliseconds(ShiftMilliseconds));
            WriteClock(Path.Combine(_root, "shifted", "raw"), sign: 1);
            Clock = ClockCorrection.Read(Path.Combine(_root, "shifted", "raw"));
        }

        internal string BaselineRaw => Path.Combine(_root, "baseline", "raw");

        internal string ShiftedRaw => Path.Combine(_root, "shifted", "raw");

        internal ClockCorrection Clock { get; }

        internal string CopyShifted(string name)
        {
            var raw = Path.Combine(_root, name, "raw");
            Copy(Path.Combine(_root, "shifted"), Path.Combine(_root, name));
            return raw;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
