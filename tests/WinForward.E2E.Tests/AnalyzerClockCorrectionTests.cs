using System.Globalization;
using WinForward.E2E.Analysis.Release;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The <c>clock.jsonl</c> parser and the correction math, through the public release seam: a campaign
/// root is read into per-pass evidence, a missing or malformed file is a status rather than an exception,
/// and one arm window is translated onto the ledger's clock by the declared sign.
/// </summary>
public sealed class AnalyzerClockCorrectionTests
{
    private static readonly DateTimeOffset s_baseUtc = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

    private static readonly double[] s_scatter = [-15.0, -5.0, 0.0, 5.0, 15.0];

    [Fact]
    public void ACompleteLogReadsBothPhasesIntoAPass()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "clock.jsonl"),
            string.Join("\n", Phases(1, 1800.0, 2200.0, TimeSpan.FromHours(1))) + "\n");

        var clock = ClockCorrection.Read(directory.Path);

        Assert.Equal(Path.Combine(directory.Path, ClockCorrection.FileName), clock.SourcePath);

        Assert.Equal(ClockStatus.Complete, clock.Status);
        Assert.True(clock.Corrected);
        Assert.Equal(10, clock.Samples);
        Assert.Equal(0, clock.BadLines);
        Assert.Null(clock.Problem);

        var pass = Assert.Single(clock.Passes);
        Assert.Equal("pass1", pass.PassId);
        Assert.Equal(1, pass.Pass);
        Assert.Equal(ClockPhaseKind.Interpolated, pass.Kind);
        Assert.Equal(10, pass.Samples);
        Assert.Equal(1800.0, pass.PreOffsetMs!.Value, 6);
        Assert.Equal(2200.0, pass.PostOffsetMs!.Value, 6);
        Assert.Equal(400.0, pass.DriftMs!.Value, 6);
        Assert.InRange(pass.ResidualMs!.Value, 14.9, 15.0);
        Assert.Equal(2000.0, pass.OffsetMs!.Value, 6);
    }

    [Fact]
    public void TheDriftLineInterpolatesAtAVmInstant()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "clock.jsonl"),
            string.Join("\n", Phases(1, 1800.0, 2200.0, TimeSpan.FromHours(1))) + "\n");

        var clock = ClockCorrection.Read(directory.Path);
        var pass = Assert.Single(clock.Passes);

        Assert.Equal(1800.0, clock.OffsetAt("pass1", pass.PreAnchorUtc!.Value)!.Value, 6);
        Assert.Equal(2200.0, clock.OffsetAt("pass1", pass.PostAnchorUtc!.Value)!.Value, 6);
        var midpoint = pass.PreAnchorUtc!.Value.AddTicks(
            (pass.PostAnchorUtc!.Value.UtcTicks - pass.PreAnchorUtc.Value.UtcTicks) / 2);
        Assert.Equal(2000.0, clock.OffsetAt("pass1", midpoint)!.Value, 6);

        Assert.Null(clock.OffsetAt("pass2", s_baseUtc));
    }

    [Fact]
    public void TheAppliedOffsetIsThePassMidpointWhileOffsetAtInterpolates()
    {
        using var directory = new TemporaryDirectory();
        // A ten-second pass whose drift line runs from 0 ms at the pre probe to 100 ms at the post probe:
        // a quarter, half and three quarters into the pass the line reads 25, 50 and 75 ms.
        var pre = new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
        var post = pre.AddSeconds(10);
        string[] lines =
        [
            Line(1, "pre", 0.0, pre, pre),
            Line(1, "post", 100.0, post, post.AddMilliseconds(-100.0)),
        ];
        File.WriteAllText(Path.Combine(directory.Path, "clock.jsonl"), string.Join("\n", lines) + "\n");

        var clock = ClockCorrection.Read(directory.Path);
        var pass = clock.Pass("pass1")!;

        Assert.Equal(ClockPhaseKind.Interpolated, pass.Kind);
        Assert.Equal(100.0, pass.DriftMs!.Value, 6);
        Assert.Equal(50.0, pass.OffsetMs!.Value, 6);
        Assert.Equal(25.0, clock.OffsetAt("pass1", pre.AddSeconds(2.5))!.Value, 6);
        Assert.Equal(50.0, clock.OffsetAt("pass1", pre.AddSeconds(5.0))!.Value, 6);
        Assert.Equal(75.0, clock.OffsetAt("pass1", pre.AddSeconds(7.5))!.Value, 6);

        foreach (var seconds in new[] { 2.5, 5.0, 7.5 })
        {
            var midpoint = pre.AddSeconds(seconds);
            var window = clock.Apply("pass1", midpoint.AddSeconds(-1), midpoint.AddSeconds(1));

            Assert.Equal(50.0, window.OffsetMs, 6);
            Assert.True(
                Math.Abs(pass.OffsetMs.Value - clock.OffsetAt("pass1", midpoint)!.Value) <= pass.DriftMs!.Value / 2,
                "the applied pass offset is within half the drift of the interpolated one");
        }
    }

    [Fact]
    public void APositiveOffsetMovesTheWindowBack()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "clock.jsonl"),
            string.Join("\n", Phases(1, 2000.0, 2000.0, TimeSpan.Zero)) + "\n");

        var clock = ClockCorrection.Read(directory.Path);
        var start = s_baseUtc.AddMinutes(5);
        var end = start.AddSeconds(30);

        var window = clock.Apply("pass1", start, end);

        Assert.True(window.Corrected);
        Assert.Equal("pass1", window.PassId);
        Assert.Equal(start, window.VmStart);
        Assert.Equal(end, window.VmEnd);
        Assert.Equal(ClockPhaseKind.Interpolated, window.Kind);
        Assert.Equal(2000.0, window.OffsetMs, 6);
        Assert.Equal(start.AddMilliseconds(-2000.0), window.HostStart);
        Assert.Equal(end.AddMilliseconds(-2000.0), window.HostEnd);
        Assert.Equal(TimeSpan.FromSeconds(-2), window.Shift);
    }

    [Fact]
    public void AMissingLogIsAbsentAndLeavesTheWindowAlone()
    {
        using var directory = new TemporaryDirectory();

        var clock = ClockCorrection.Read(directory.Path);

        Assert.Equal(ClockStatus.Absent, clock.Status);
        Assert.False(clock.Corrected);
        Assert.Empty(clock.Passes);
        Assert.Equal(0, clock.Samples);
        Assert.Equal(0, clock.BadLines);
        Assert.Null(clock.Pass("pass1"));
        Assert.Null(clock.OffsetAt("pass1", s_baseUtc));

        var start = s_baseUtc.AddMinutes(5);
        var end = start.AddSeconds(30);
        var window = clock.Apply("pass1", start, end);

        Assert.False(window.Corrected);
        Assert.Equal(ClockPhaseKind.Missing, window.Kind);
        Assert.Equal(start, window.HostStart);
        Assert.Equal(end, window.HostEnd);
        Assert.Equal(0.0, window.OffsetMs);
        Assert.Equal(TimeSpan.Zero, window.Shift);
    }

    [Fact]
    public void APassWithOnePhaseIsPartialAndUsesThatPhase()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "clock.jsonl"),
            string.Join("\n", Line(1, "pre", 1234.5, s_baseUtc, s_baseUtc.AddMilliseconds(-1234.5)))
            + "\n"
            + string.Join("\n", Line(2, "post", -700.25, s_baseUtc.AddHours(1), s_baseUtc.AddHours(1).AddMilliseconds(700.25)))
            + "\n");

        var clock = ClockCorrection.Read(directory.Path);

        Assert.Equal(ClockStatus.Partial, clock.Status);
        Assert.True(clock.Corrected);
        Assert.Equal(2, clock.Passes.Count);

        var preOnly = clock.Pass("pass1")!;
        Assert.Equal(ClockPhaseKind.PreOnly, preOnly.Kind);
        Assert.Equal(1234.5, preOnly.PreOffsetMs!.Value, 6);
        Assert.Null(preOnly.PostOffsetMs);
        Assert.Null(preOnly.DriftMs);
        Assert.Equal(0.0, preOnly.ResidualMs!.Value, 6);
        Assert.Equal(1234.5, preOnly.OffsetMs!.Value, 6);

        var postOnly = clock.Pass("pass2")!;
        Assert.Equal(ClockPhaseKind.PostOnly, postOnly.Kind);
        Assert.Equal(-700.25, postOnly.PostOffsetMs!.Value, 6);
        Assert.Equal(-700.25, postOnly.OffsetMs!.Value, 6);

        var window = clock.Apply("pass1", s_baseUtc, s_baseUtc.AddSeconds(1));
        Assert.Equal(s_baseUtc.AddMilliseconds(-1234.5), window.HostStart);
    }

    [Fact]
    public void AMalformedLineIsCountedAndTheRestStillRead()
    {
        using var directory = new TemporaryDirectory();
        // One pre and one post probe survive; every line after them is malformed for a different reason.
        var valid = Phases(1, 100.0, 200.0, TimeSpan.FromHours(1)).Where((_, index) => index is 0 or 5).ToList();

        string[] lines =
        [
            .. valid,
            "not json",
            "{\"pass\":1,\"phase\":\"pre\"}",
            "{\"pass\":\"1\",\"phase\":\"pre\",\"offsetMs\":100,\"rttMs\":0.4,\"vmUtc\":\"2026-10-06T08:00:00.000000+00:00\",\"hostUtc\":\"2026-10-06T08:00:00.000000+00:00\"}",
            "{\"pass\":1,\"phase\":\"PRE\",\"offsetMs\":100}",
            "{\"pass\":1,\"phase\":\"pre\",\"offsetMs\":100,\"rttMs\":0.4,\"vmUtc\":\"not-a-stamp\",\"hostUtc\":\"2026-10-06T08:00:00.000000+00:00\"}",
        ];
        File.WriteAllText(Path.Combine(directory.Path, "clock.jsonl"), string.Join("\n", lines) + "\n");

        var clock = ClockCorrection.Read(directory.Path);

        Assert.Equal(ClockStatus.Complete, clock.Status);
        Assert.Equal(2, clock.Samples);
        Assert.Equal(5, clock.BadLines);
        Assert.Null(clock.Problem);
    }

    [Fact]
    public void AFileWithNoUsableSampleIsIncomplete()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "clock.jsonl"),
            "not json\n{\"pass\":0,\"phase\":\"pre\"}\n{\"pass\":1,\"phase\":\"pre\",\"offsetMs\":1e12}\n");

        var clock = ClockCorrection.Read(directory.Path);

        Assert.Equal(ClockStatus.Incomplete, clock.Status);
        Assert.False(clock.Corrected);
        Assert.Empty(clock.Passes);
        Assert.Equal(0, clock.Samples);
        Assert.Equal(3, clock.BadLines);

        var window = clock.Apply("pass1", s_baseUtc, s_baseUtc.AddSeconds(30));
        Assert.False(window.Corrected);
        Assert.Equal(s_baseUtc, window.HostStart);
    }

    [Fact]
    public void AnUnknownMemberDoesNotInvalidateAProbe()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "clock.jsonl"),
            "{\"pass\":3,\"phase\":\"post\",\"offsetMs\":250.5,\"rttMs\":0.4,"
            + "\"vmUtc\":\"2026-10-06T09:00:00.000000+00:00\",\"hostUtc\":\"2026-10-06T09:00:00.000000+00:00\","
            + "\"seq\":7,\"note\":\"a later writer's member\"}\n");

        var clock = ClockCorrection.Read(directory.Path);

        Assert.Equal(0, clock.BadLines);
        Assert.Equal(1, clock.Samples);
        Assert.Equal(ClockStatus.Partial, clock.Status);
        Assert.Equal(ClockPhaseKind.PostOnly, clock.Pass("pass3")!.Kind);
    }

    [Fact]
    public void TheResidualIsTheLargestDeviationFromTheLine()
    {
        using var directory = new TemporaryDirectory();
        // Two probes a phase, medians 200 and 400, samples 100 either side of their median: the line is
        // the medians' line, so the scatter is the residual.
        string[] lines =
        [
            Line(1, "pre", 100.0, s_baseUtc, s_baseUtc.AddMilliseconds(-100.0)),
            Line(1, "pre", 300.0, s_baseUtc.AddMilliseconds(150.0), s_baseUtc.AddMilliseconds(150.0 - 300.0)),
            Line(1, "post", 300.0, s_baseUtc.AddHours(1), s_baseUtc.AddHours(1).AddMilliseconds(-300.0)),
            Line(1, "post", 500.0, s_baseUtc.AddHours(1).AddMilliseconds(150.0), s_baseUtc.AddHours(1).AddMilliseconds(150.0 - 500.0)),
        ];
        File.WriteAllText(Path.Combine(directory.Path, "clock.jsonl"), string.Join("\n", lines) + "\n");

        var clock = ClockCorrection.Read(directory.Path);

        var pass = clock.Pass("pass1")!;
        Assert.Equal(200.0, pass.PreOffsetMs!.Value, 6);
        Assert.Equal(400.0, pass.PostOffsetMs!.Value, 6);
        Assert.Equal(200.0, pass.DriftMs!.Value, 6);
        Assert.InRange(pass.ResidualMs!.Value, 99.9, 100.0);
    }

    private static IEnumerable<string> Phases(int pass, double preOffset, double postOffset, TimeSpan span)
    {
        for (var index = 0; index < s_scatter.Length; index++)
        {
            var vm = s_baseUtc.AddMilliseconds(index * 150.0);
            yield return Line(pass, "pre", preOffset + s_scatter[index], vm, vm.AddMilliseconds(-preOffset - s_scatter[index]));
        }

        for (var index = 0; index < s_scatter.Length; index++)
        {
            var vm = s_baseUtc.Add(span).AddMilliseconds(index * 150.0);
            yield return Line(pass, "post", postOffset + s_scatter[index], vm, vm.AddMilliseconds(-postOffset - s_scatter[index]));
        }
    }

    private static string Line(int pass, string phase, double offsetMs, DateTimeOffset vmUtc, DateTimeOffset hostUtc) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"pass\":{pass},\"phase\":\"{phase}\",\"offsetMs\":{offsetMs:0.###},\"rttMs\":0.4,\"vmUtc\":\"{Stamp(vmUtc)}\",\"hostUtc\":\"{Stamp(hostUtc)}\"}}");

    private static string Stamp(DateTimeOffset stamp) =>
        stamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wf-clock-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
