using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Metrics;

/// <summary>
/// One metric's declaration: how it is labelled and rounded, which arm it is read from, which traffic
/// path its value rides on, and the extraction that reads it out of an arm's records.
/// </summary>
/// <param name="Key">The metric's identifier, which is also its member name in <c>verdict.json</c>.</param>
/// <param name="Label">The label the report prints.</param>
/// <param name="Unit">The unit legend: <c>pp</c>, <c>us</c>, <c>Mbps</c>, <c>%vcpu</c>, <c>count</c>.</param>
/// <param name="Digits">How many digits the headline matrix keeps after the point for this metric.</param>
/// <param name="Family">Which pre-declared threshold family judges its comparisons.</param>
/// <param name="Definition">The definition the report publishes beside the numbers.</param>
/// <param name="Arm">The arm the metric is read from, or null for a metric read from the samples.</param>
/// <param name="UdpPath"><c>udp</c> for the UDP echo arms, <c>dns</c> for a DNS arm, else null.</param>
/// <param name="Dns53">Whether this is the port-53 DNS arm, whose carriage differs per row.</param>
/// <param name="Extract">Per-pass extraction of the metric from one run.</param>
/// <remarks>
/// <para><b>The unit is a legend, not a suffix.</b> A <c>count</c> cell prints no unit at all while a
/// <c>pp</c> cell prints <c> pp</c>; the reference decides that from the unit name rather than from a
/// second flag, and the two are not interchangeable in the compared text.</para>
/// <para><b>The rounding is the metric's own, because §4 is the only section that prints every
/// metric.</b> A section that renders one column of its own passes the digits where it prints them; the
/// headline matrix reads them from here, which is why the declaration carries them at all.</para>
/// <para><b>A metric with no threshold family is a published interval, not a verdict.</b> Throughput and
/// event counts report their confidence interval as <c>no-threshold-declared</c> instead of guessing at
/// a band nobody declared.</para>
/// </remarks>
internal sealed record MetricSpec(
    string Key,
    string Label,
    string Unit,
    int Digits,
    string Family,
    string Definition,
    string? Arm,
    string? UdpPath,
    bool Dns53,
    Func<CampaignModel, ClientRun, Measured<double?>> Extract);

/// <summary>
/// The pre-declared practical-significance thresholds: what a difference has to clear before the report
/// calls it one, and what kind of statistic the interval is.
/// </summary>
/// <param name="Kind">Ratio (the band is <c>1 ± value</c>) or diff (the band is <c>± value</c>).</param>
/// <param name="Value">The threshold itself, or null when no threshold is declared for the family.</param>
/// <param name="Text">The threshold as the report quotes it.</param>
/// <remarks>
/// The thresholds are declared constants, not tuned after seeing a result: the interval is computed
/// first and judged against the band afterwards, and the band is published in <c>verdict.json</c>'s
/// <c>thresholds</c> so a reader can see which one judged a pair.
/// </remarks>
internal sealed record ThresholdSpec(string Kind, double? Value, string Text);

/// <summary>The declared threshold families, in the reference's own spelling.</summary>
internal static class Thresholds
{
    /// <summary>A ratio metric's band: the interval has to clear <c>1 ± 5 %</c> to be called different.</summary>
    private static ThresholdSpec Latency { get; } = new(Kind: "ratio", Value: 0.05, Text: "5 % latency");

    /// <summary>A CPU ratio metric's band.</summary>
    private static ThresholdSpec Cpu { get; } = new(Kind: "ratio", Value: 0.10, Text: "10 % CPU");

    /// <summary>A memory ratio metric's band.</summary>
    private static ThresholdSpec Memory { get; } = new(Kind: "ratio", Value: 0.10, Text: "10 % memory");

    /// <summary>A loss rate's band, in percentage points.</summary>
    private static ThresholdSpec UdpLoss { get; } = new(Kind: "diff", Value: 0.5, Text: "0.5 pp UDP loss");

    /// <summary>A TCP unexpected-event rate's band, in percentage points.</summary>
    private static ThresholdSpec TcpUnexpected { get; } = new(Kind: "diff", Value: 0.1, Text: "0.1 pp TCP unexpected rate");

    /// <summary>The family of metrics nobody declared a threshold for.</summary>
    private static ThresholdSpec None { get; } = new(Kind: "ratio", Value: null, Text: "no pre-declared threshold");

    /// <summary>The family of one metric identifier.</summary>
    /// <param name="family">The metric's <c>family</c> member.</param>
    /// <returns>The pre-declared threshold that judges its comparisons.</returns>
    internal static ThresholdSpec For(string family) => family switch
    {
        "latency" => Latency,
        "cpu" => Cpu,
        "memory" => Memory,
        "udp-loss" => UdpLoss,
        "tcp-unexpected" => TcpUnexpected,
        _ => None,
    };
}
