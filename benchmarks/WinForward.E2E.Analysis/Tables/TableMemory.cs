using System.Globalization;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;
using WinForward.E2E.Contracts;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// §7, "Memory detail": one line per row, the product's steady-state private bytes and working set, the
/// peak the sampler saw, and the OLS slope that decides whether the row leaks.
/// </summary>
/// <remarks>
/// <para><b>Steady state is per arm, not per run.</b> Each arm's warmup window starts at that arm's own
/// first tick, so a long first arm does not eat into the steady state of the second; a run that never
/// published the window falls back to the whole series rather than to an empty one.</para>
/// <para><b>An unreadable sample is rejected, never counted as zero.</b> A tick the sampler could not
/// read leaves the counters null, and averaging those in would report a measurement that did not
/// happen — which is exactly the direction that hides a leak.</para>
/// <para><b>Two slopes, because a row's arms are not one process history.</b> The first regresses over
/// every arm the row ran; the second over the <c>MIX</c> arm alone, whose mixed workload is the one a
/// leak would show up in first. The verdict is a statement about the interval, not about the sign of the
/// point estimate: a CI that contains zero is reported as such.</para>
/// </remarks>
internal static class TableMemory
{
    private const string NoSteadySamples = "n/a (no steady-state product samples)";

    private const string NoArmSlope = "n/a (fewer than three samples per pass)";

    private const string NoMixSlope = "n/a (no MIX samples)";

    private const string MixArm = "MIX";

    private const string Unavailable = "n/a";

    private static readonly string[] s_headers =
    [
        "row",
        "passes",
        "steady samples/pass",
        "private p50",
        "private p95",
        "working set p50",
        "working set p95",
        "peak working set",
        "slope all arms (MiB/min)",
        "slope verdict",
        "slope MIX (MiB/min)",
        "slope MIX verdict",
    ];

    /// <summary>The section's body, without its heading and ending in a newline.</summary>
    /// <param name="campaign">The loaded campaign every sample is read from.</param>
    /// <returns>The body's text.</returns>
    internal static string RenderBody(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var rows = new List<IReadOnlyList<string>>(campaign.RowIds.Count);
        foreach (var rowId in campaign.RowIds)
        {
            rows.Add(Line(campaign, rowId));
        }

        var lines = new List<string>
        {
            Caption(campaign.WarmupSeconds),
            string.Empty,
            MarkdownTable.Render(s_headers, rows),
            string.Empty,
        };

        return string.Join('\n', lines);
    }

    /// <summary>What the section measures, and the one substitution the paragraph carries.</summary>
    private static string Caption(double warmupSeconds)
    {
        var warmup = VerbatimNumber.Fixed(warmupSeconds, 1);
        return $"Steady state = the product's own readable samples after the first {warmup} s of each arm (per-arm window from "
               + "`run.json`); `absent` ticks are excluded and a sample carrying `readError` is rejected outright, because its "
               + "counters are `null` rather than `0`. `private p50/p95` and `working set p50/p95` are the per-pass p50/p95 of "
               + "that pass's steady-state samples, then `median [p25–p75] across passes (n=K)`. `peak working set` is the "
               + "largest `peakWorkingSetBytes` the sampler reported (a monotone process peak). The OLS slope regresses private "
               + "bytes (MiB) on elapsed time within the pass and is reported in MiB/min with its 95 % CI; the verdict is "
               + "*leaks* when that CI excludes zero from above. Two slopes are given: all measured arms of the row, and the "
               + "`MIX` arm alone. The CI uses Student's t for df 1–30 and 1.96 beyond.";
    }

    /// <summary>One row's line: the per-pass medians across its arms, or the reason there are none.</summary>
    private static List<string> Line(CampaignModel campaign, string rowId)
    {
        var row = Read(campaign, rowId);
        if (row.PrivateP50.Count == 0)
        {
            return [rowId, "0", NoSteadySamples, .. Absent(9)];
        }

        return
        [
            rowId,
            row.PrivateP50.Count.ToString(CultureInfo.InvariantCulture),
            row.Counts.Count > 0 ? DescriptiveStats.FmtStat(row.Counts, 0) : Unavailable,
            DescriptiveStats.FmtStat(row.PrivateP50, 2, " MiB"),
            DescriptiveStats.FmtStat(row.PrivateP95, 2, " MiB"),
            row.WorkingP50.Count > 0 ? DescriptiveStats.FmtStat(row.WorkingP50, 2, " MiB") : Unavailable,
            row.WorkingP95.Count > 0 ? DescriptiveStats.FmtStat(row.WorkingP95, 2, " MiB") : Unavailable,
            row.Peaks.Count > 0 ? DescriptiveStats.FmtStat(row.Peaks, 2, " MiB") : Unavailable,
            row.Slopes.Count > 0 ? DescriptiveStats.FmtStat(row.Slopes, 3, " MiB/min") : NoArmSlope,
            DescriptiveStats.SummariseVerdicts(row.SlopeVerdicts),
            row.MixSlopes.Count > 0 ? DescriptiveStats.FmtStat(row.MixSlopes, 3, " MiB/min") : NoMixSlope,
            DescriptiveStats.SummariseVerdicts(row.MixVerdicts),
        ];
    }

    /// <summary>Every pass's contribution to one row's line.</summary>
    private static MemoryReading Read(CampaignModel campaign, string rowId)
    {
        var reading = new MemoryReading();
        foreach (var passId in campaign.PassIds)
        {
            if (campaign.InPass(passId, rowId) is { } row
                && RunSamples.PrimaryProductProcess(row) is { } primary)
            {
                Pass(reading, row, primary, campaign.WarmupSeconds);
            }
        }

        return reading;
    }

    /// <summary>One pass of one row: the per-pass quantiles the across-pass medians are taken over.</summary>
    private static void Pass(MemoryReading reading, ClientRun row, string primary, double warmupSeconds)
    {
        var arms = new MemorySeries(row, primary, warmupSeconds);
        foreach (var arm in row.Arms.All)
        {
            arms.Add(arm.Name);
        }

        reading.Counts.Add(arms.Private.Count);
        AddQuantiles(reading.PrivateP50, reading.PrivateP95, arms.Private);
        AddQuantiles(reading.WorkingP50, reading.WorkingP95, arms.Working);
        if (arms.Peaks.Count > 0)
        {
            reading.Peaks.Add(arms.Peaks.Max());
        }

        if (Leak(row, arms) is { } slope)
        {
            reading.Slopes.Add(slope.MiBPerMinute);
            reading.SlopeVerdicts.Add(slope.Verdict);
        }

        var mix = new MemorySeries(row, primary, warmupSeconds);
        mix.Add(MixArm);
        if (Leak(row, mix) is { } mixSlope)
        {
            reading.MixSlopes.Add(mixSlope.MiBPerMinute);
            reading.MixVerdicts.Add(mixSlope.Verdict);
        }
    }

    /// <summary>One pass's p50 and p95 of a series, added only when the series has anything in it.</summary>
    private static void AddQuantiles(List<double> p50, List<double> p95, List<double> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        p50.Add(DescriptiveStats.Quantile(values, 0.50)!.Value);
        p95.Add(DescriptiveStats.Quantile(values, 0.95)!.Value);
    }

    /// <summary>
    /// One pass's leak fit: the private bytes regressed on elapsed time, or null when the pass could not
    /// place its samples in time or had too few of them.
    /// </summary>
    private static (double MiBPerMinute, string Verdict)? Leak(ClientRun row, MemorySeries series)
    {
        if (series.Ticks.Count < 3 || RunClocks.TickFrequency(row) is not { } frequency)
        {
            return null;
        }

        var origin = series.Ticks.Min();
        var seconds = series.Ticks.ConvertAll(tick => (tick - origin) / frequency);
        var fit = OlsSlope.Fit(seconds, series.SlopeValues);
        if (fit.Slope is not { } slope)
        {
            return null;
        }

        var low = VerbatimNumber.Fixed(fit.Low!.Value * 60.0, 2);
        var high = VerbatimNumber.Fixed(fit.High!.Value * 60.0, 2);
        return (slope * 60.0, $"{fit.Verdict} [{low}–{high}]");
    }

    /// <summary>One value repeated for a row whose passes had no steady-state sample at all.</summary>
    private static string[] Absent(int count)
    {
        var cells = new string[count];
        Array.Fill(cells, Unavailable);
        return cells;
    }

    /// <summary>Every pass's contribution to one row's line.</summary>
    private sealed class MemoryReading
    {
        internal List<double> PrivateP50 { get; } = [];

        internal List<double> PrivateP95 { get; } = [];

        internal List<double> WorkingP50 { get; } = [];

        internal List<double> WorkingP95 { get; } = [];

        internal List<double> Peaks { get; } = [];

        internal List<double> Counts { get; } = [];

        internal List<double> Slopes { get; } = [];

        internal List<string> SlopeVerdicts { get; } = [];

        internal List<double> MixSlopes { get; } = [];

        internal List<string> MixVerdicts { get; } = [];
    }

    /// <summary>
    /// One row's steady-state series for one pass, in MiB, arm by arm in load order: the values the
    /// quantiles are taken over and the time series the leak fit is drawn from.
    /// </summary>
    /// <param name="row">The run whose arms are read.</param>
    /// <param name="primary">The process every sample must belong to.</param>
    /// <param name="warmupSeconds">How many seconds of each arm to discard.</param>
    private sealed class MemorySeries(ClientRun row, string primary, double warmupSeconds)
    {
        /// <summary>Every readable private-bytes reading, including those with no tick beside them.</summary>
        internal List<double> Private { get; } = [];

        internal List<double> Working { get; } = [];

        internal List<double> Peaks { get; } = [];

        /// <summary>The ticks paired, one for one, with <see cref="SlopeValues"/>.</summary>
        internal List<double> Ticks { get; } = [];

        /// <summary>The private bytes the leak fit regresses, paired with <see cref="Ticks"/>.</summary>
        internal List<double> SlopeValues { get; } = [];

        /// <summary>Reads one arm's steady-state samples into the pass's series.</summary>
        internal void Add(string armName)
        {
            foreach (var sample in RunSamples.SteadySamples(row, armName, primary, warmupSeconds))
            {
                var value = RunSamples.ProcessPrivateBytes(sample);
                if (value is { } bytes)
                {
                    Private.Add(bytes / RunSamples.Mebibyte);
                }

                if (JsonValue.Number(sample, ArmKeys.Sample.Counters.WorkingSetBytes) is { } working)
                {
                    Working.Add(working / RunSamples.Mebibyte);
                }

                if (JsonValue.Number(sample, ArmKeys.Sample.Counters.PeakWorkingSetBytes) is { } peak)
                {
                    Peaks.Add(peak / RunSamples.Mebibyte);
                }

                if (value is { } slopeValue && JsonValue.Number(sample, ArmKeys.Sample.Ticks) is { } tick)
                {
                    Ticks.Add(tick);
                    SlopeValues.Add(slopeValue / RunSamples.Mebibyte);
                }
            }
        }
    }
}
