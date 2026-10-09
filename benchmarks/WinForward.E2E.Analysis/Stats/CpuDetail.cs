using System.Text.Json;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Contracts;

namespace WinForward.E2E.Analysis.Stats;

/// <summary>
/// What one CPU reading did, for the columns that report the reading's own shape rather than its
/// value: how many process identities the samples carried, how many of them could be used, and how many
/// distinct process starts were seen.
/// </summary>
/// <param name="Identities">How many process identities the arm's samples described.</param>
/// <param name="IdentitiesUsed">How many of them contributed a usable delta.</param>
/// <param name="Restarts">How many process starts beyond the first the series showed.</param>
internal sealed record CpuDiagnostics(int Identities, int IdentitiesUsed, int Restarts);

/// <summary>
/// One arm's CPU as a share of one logical processor, summed per process identity, plus the reasons a
/// reading can fail and the diagnostics printed beside it.
/// </summary>
/// <remarks>
/// <para><b>Per identity, not per process name.</b> Each <c>(pid, startUtc)</c> contributes the delta of
/// its own cumulative <c>cpuSeconds</c> between its first and last readable sample and the deltas are
/// summed, so a process that restarted mid-run cannot corrupt the total the way a first/last difference
/// over a process <i>name</i> would subtract one process's counter from another's.</para>
/// <para><b>Unreadable counters are rejected, never averaged as zero.</b> The harness leaves the
/// counters null in a sample whose <c>readError</c> is set; a zero would pull every cell down.</para>
/// <para><b>Legacy records fall back to the record's own total.</b> A sample with no per-process block
/// carries only the summed counter, and those samples are one pseudo-identity rather than a gap.</para>
/// </remarks>
internal static class CpuDetail
{
    /// <summary>The reason a reading has no frequency to convert ticks with.</summary>
    private const string NoTickFrequency = "no tick frequency in run.json";

    /// <summary>The reason a reading has too few samples to difference.</summary>
    private const string TooFewSamples = "fewer than two readable samples";

    /// <summary>The reason a reading's ticks do not advance.</summary>
    private const string NonIncreasingTicks = "non-increasing sample ticks";

    /// <summary>The reason no identity carried two readable counters.</summary>
    private const string NoIdentity = "no process identity with two readable samples";

    /// <summary>The counter a product sample's own CPU total is read from.</summary>
    private const string ProductField = "cpuSeconds";

    /// <summary>The counter the sampler's own CPU total is read from.</summary>
    internal const string GeneratorField = "generatorCpuSeconds";

    /// <summary>The identity the fallback series is filed under, which no real process can share.</summary>
    private static readonly ProcessIdentity s_legacyIdentity = new("sum", "n/a (no per-process block)");

    /// <summary>One arm's CPU as a percentage of one vCPU, or the reason it has none.</summary>
    /// <param name="samples">The arm's samples, in file order.</param>
    /// <param name="tickFrequency">The run's own ticks-per-second, or null when it published none.</param>
    /// <param name="fallbackField">The record-level counter a sample with no process block is read from.</param>
    internal static (double? Value, string? Reason, CpuDiagnostics Diagnostics) Compute(
        IReadOnlyList<JsonElement> samples,
        double? tickFrequency,
        string fallbackField = ProductField)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(fallbackField);

        if (tickFrequency is null)
        {
            return (null, NoTickFrequency, new CpuDiagnostics(0, 0, 0));
        }

        var usable = samples
            .Where(sample => RunSamples.IsReadable(sample) && JsonValue.Number(sample, ArmKeys.Sample.Ticks) is not null)
            .ToList();
        if (usable.Count < 2)
        {
            return (null, TooFewSamples, new CpuDiagnostics(0, 0, 0));
        }

        var (series, fallback) = Collect(usable, fallbackField);
        if (series.Count == 0 && fallback.Count > 0)
        {
            series.Add((s_legacyIdentity, fallback));
        }

        var restarts = Math.Max(
            0,
            series.Select(entry => entry.Key.StartUtc).Distinct(StringComparer.Ordinal).Count() - 1);
        var (lowest, highest) = Bounds(usable);
        if (highest <= lowest)
        {
            return (null, NonIncreasingTicks, new CpuDiagnostics(series.Count, 0, restarts));
        }

        var (total, used) = Accumulate(series);
        return used == 0
            ? (null, NoIdentity, new CpuDiagnostics(series.Count, 0, restarts))
            : (100.0 * total * tickFrequency.Value / (highest - lowest), null, new CpuDiagnostics(series.Count, used, restarts));
    }

    /// <summary>The tick span the usable samples cover, which is the divisor the rate is measured over.</summary>
    private static (double Lowest, double Highest) Bounds(IReadOnlyList<JsonElement> samples)
    {
        var lowest = double.MaxValue;
        var highest = double.MinValue;
        foreach (var sample in samples)
        {
            var ticks = JsonValue.Number(sample, ArmKeys.Sample.Ticks)!.Value;
            lowest = Math.Min(lowest, ticks);
            highest = Math.Max(highest, ticks);
        }

        return (lowest, highest);
    }

    /// <summary>
    /// The seconds every identity with two readable counters contributed, and how many identities those
    /// were — an identity whose counter went backwards is dropped rather than subtracted.
    /// </summary>
    private static (double Total, int Used) Accumulate(
        List<(ProcessIdentity Key, List<(double Ticks, double Cpu)> Points)> series)
    {
        var total = 0.0;
        var used = 0;
        foreach (var (_, points) in series)
        {
            points.Sort();
            if (points.Count < 2)
            {
                continue;
            }

            var delta = points[^1].Cpu - points[0].Cpu;
            if (delta < 0.0)
            {
                continue;
            }

            total += delta;
            used++;
        }

        return (total, used);
    }

    /// <summary>
    /// The CPU seconds one arm's samples accumulate, summed over the identities that carry two readable
    /// counters — the numerator the per-transaction columns divide by a denominator.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Compute"/> this reads no fallback: the per-transaction column is about the
    /// sampled process's own CPU, and a record-level total would not say whose time it was.
    /// </remarks>
    /// <param name="samples">The arm's present samples.</param>
    /// <returns>The seconds, or null when no identity accumulated any.</returns>
    internal static double? IdentityCpuSeconds(IReadOnlyList<JsonElement> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var (series, _) = Collect(samples, fallbackField: null);
        var deltas = new List<double>(series.Count);
        foreach (var (_, points) in series)
        {
            points.Sort();
            if (points.Count >= 2 && points[^1].Cpu >= points[0].Cpu)
            {
                deltas.Add(points[^1].Cpu - points[0].Cpu);
            }
        }

        var total = DescriptiveStats.Sum(deltas);
#pragma warning disable S1244 // The reference tests the accumulated seconds against an exact zero here.
        return total != 0.0 ? total : null;
#pragma warning restore S1244
    }

    /// <summary>
    /// The per-identity counter series of one arm, in the order each identity first appeared, plus the
    /// record-level series a sample with no process block contributes.
    /// </summary>
    /// <param name="samples">The samples to read.</param>
    /// <param name="fallbackField">The record-level counter, or null to ignore those samples.</param>
    private static (List<(ProcessIdentity Key, List<(double Ticks, double Cpu)> Points)> Series,
        List<(double Ticks, double Cpu)> Fallback) Collect(
        IReadOnlyList<JsonElement> samples,
        string? fallbackField)
    {
        var series = new List<(ProcessIdentity Key, List<(double Ticks, double Cpu)> Points)>();
        var index = new Dictionary<ProcessIdentity, List<(double Ticks, double Cpu)>>();
        var fallback = new List<(double Ticks, double Cpu)>();
        foreach (var sample in samples)
        {
            if (JsonValue.Number(sample, ArmKeys.Sample.Ticks) is not { } ticks)
            {
                continue;
            }

            var identities = RunSamples.SampleIdentities(sample);
            if (identities.Count == 0)
            {
                if (fallbackField is not null && JsonValue.Number(sample, fallbackField) is { } value)
                {
                    fallback.Add((ticks, value));
                }

                continue;
            }

            foreach (var (identity, cpu, read) in identities)
            {
                if (!read || cpu is null)
                {
                    continue;
                }

                if (!index.TryGetValue(identity, out var points))
                {
                    points = [];
                    index[identity] = points;
                    series.Add((identity, points));
                }

                points.Add((ticks, cpu.Value));
            }
        }

        return (series, fallback);
    }
}
