using System.Globalization;
using WinForward.E2E.Analysis.Json;

namespace WinForward.E2E.Analysis.Stats;

/// <summary>
/// The descriptive statistics every table cell is built from — the linear-interpolation percentile, the
/// median with its interquartile range, and the <c>median [p25–p75] unit (n=K)</c> rendering of a
/// metric's per-pass values.
/// </summary>
/// <remarks>
/// <para><b>One percentile helper.</b> The harness's own histograms are never recomputed; this quantile
/// is what turns a metric's per-pass values into a cell, and it is the only one in the analysis.</para>
/// <para><b>The rule of three.</b> A cell whose every pass value is exactly zero prints <c>&lt; 3/n</c>
/// rather than a misleading <c>0</c>; a cell whose median is zero but which has a non-zero pass keeps the
/// ordinary rendering so the spread stays visible.</para>
/// <para><b>A null pass is counted, not dropped.</b> A rate the harness wrote as JSON null has a zero
/// denominator; when every pass is null the cell is empty, and when only some are the count is printed
/// beside the pass count.</para>
/// </remarks>
internal static class DescriptiveStats
{
    /// <summary>Linear-interpolation percentile over already-collected values; <paramref name="q"/> is in [0, 1].</summary>
    internal static double? Quantile(IReadOnlyList<double> values, double q)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return null;
        }

        var ordered = values.Order().ToList();
        if (ordered.Count == 1)
        {
            return ordered[0];
        }

        var position = q * (ordered.Count - 1);
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        return low == high
            ? ordered[low]
            : ordered[low] + ((ordered[high] - ordered[low]) * (position - low));
    }

    /// <summary>The median of the values, or null when there are none.</summary>
    internal static double? Median(IReadOnlyList<double> values) => Quantile(values, 0.5);

    /// <summary>The median and its interquartile range, or nulls when there are no values.</summary>
    private static (double? Median, double? P25, double? P75) MedianIqr(IReadOnlyList<double> values) =>
        (Quantile(values, 0.5), Quantile(values, 0.25), Quantile(values, 0.75));

    /// <summary>One metric's per-pass values as the reference renders them.</summary>
    internal static string FmtStat(
        IReadOnlyList<double> values,
        int digits = 3,
        string unit = "",
        int? zeroBoundN = null,
        double? boundScale = null,
        int nullPasses = 0,
        int missingPasses = 0)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(unit);

        if (values.Count == 0)
        {
            return nullPasses > 0 ? string.Empty : "n/a";
        }

        var unavailable = nullPasses + missingPasses;
        if (zeroBoundN is > 0 && values.All(IsZero))
        {
            var suffix = unavailable == 0
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $" ({unavailable} pass(es) without a value)");
            if (boundScale is null)
            {
                return string.Create(CultureInfo.InvariantCulture, $"< 3/{zeroBoundN}{suffix}");
            }

            var bound = VerbatimNumber.Cell(3.0 * boundScale.Value / zeroBoundN.Value, 4, unit);
            return string.Create(CultureInfo.InvariantCulture, $"< 3/{zeroBoundN} = {bound}{suffix}");
        }

        var (median, p25, p75) = MedianIqr(values);
        var total = values.Count + nullPasses + missingPasses;
        var parts = new List<string>(2);
        if (nullPasses > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{nullPasses} null"));
        }

        if (missingPasses > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{missingPasses} unavailable"));
        }

        string tail;
        if (parts.Count > 0)
        {
            tail = $"(n={values.Count.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)}; {string.Join(", ", parts)})";
        }
        else
        {
            tail = values.Count == 1 ? "(n=1)" : $"(n={values.Count.ToString(CultureInfo.InvariantCulture)})";
        }

        return values.Count == 1
            ? $"{VerbatimNumber.Cell(median, digits, unit)} {tail}"
            : $"{VerbatimNumber.Cell(median, digits)} [{VerbatimNumber.Cell(p25, digits)}–{VerbatimNumber.Cell(p75, digits)}]{unit} {tail}";
    }

    /// <summary>
    /// The sum of a float series the way CPython's <c>sum</c> computes it: the running total with
    /// Neumaier compensation, which is what keeps <c>1e16 + 1 - 1e16</c> from losing the middle term.
    /// </summary>
    /// <remarks>
    /// A least-squares slope is a difference of large sums, so the last printed digit of §7's slope
    /// cells depends on how the sums were accumulated. A plain loop is the wrong sum here: the
    /// reference's <c>sum</c> compensates, and <c>total += value</c> does not (see
    /// <see cref="OlsSlope"/>).
    /// </remarks>
    /// <param name="values">The values to add, in the order they are added.</param>
    /// <returns>The compensated sum.</returns>
    internal static double Sum(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var total = 0.0;
        var compensation = 0.0;
        foreach (var value in values)
        {
            var sum = total + value;
            compensation += Math.Abs(total) >= Math.Abs(value)
                ? (total - sum) + value
                : (value - sum) + total;
            total = sum;
        }

        return total + compensation;
    }

    /// <summary>One row's per-pass verdicts as one cell: the verdict itself, or a mixed summary.</summary>
    internal static string SummariseVerdicts(IReadOnlyList<string> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);

        if (verdicts.Count == 0)
        {
            return "n/a";
        }

        var distinct = verdicts.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return distinct.Count == 1 ? distinct[0] : $"mixed across passes: {string.Join("; ", distinct)}";
    }

    /// <summary>
    /// The Holm–Bonferroni step-down adjustment over one metric's family of comparable pair
    /// comparisons: the p-values in their own order, each replaced by the largest value the step-down
    /// has reached, capped at one.
    /// </summary>
    /// <param name="pvalues">One p-value per comparison, in the family's own order.</param>
    /// <returns>The adjusted p-values, in the order they were given.</returns>
    internal static List<double> HolmAdjust(IReadOnlyList<double> pvalues)
    {
        ArgumentNullException.ThrowIfNull(pvalues);

        var count = pvalues.Count;
        var adjusted = new List<double>(count);
        for (var index = 0; index < count; index++)
        {
            adjusted.Add(1.0);
        }

        var order = Enumerable.Range(0, count).OrderBy(index => pvalues[index]).ToList();
        var running = 0.0;
        for (var rank = 0; rank < order.Count; rank++)
        {
            var index = order[rank];
            running = Math.Max(running, Math.Min(1.0, (count - rank) * pvalues[index]));
            adjusted[index] = running;
        }

        return adjusted;
    }

    /// <summary>Whether a value is exactly zero, which is the reference's own comparison.</summary>
    private static bool IsZero(double value) => value.Equals(0.0);

    /// <summary>Whether an observed count agrees with an expected one inside a relative-or-absolute band.</summary>
    internal static bool? WithinTolerance(double? observed, double? expected, double relative, double slack) =>
        observed is null || expected is null
            ? null
            : Math.Abs(observed.Value - expected.Value) <= Math.Max(slack, relative * Math.Abs(expected.Value));
}
