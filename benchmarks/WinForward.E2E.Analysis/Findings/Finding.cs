namespace WinForward.E2E.Analysis.Findings;

/// <summary>
/// One finding: a correctness failure, a path-interference, a harness error, a measurement caveat or an
/// informational note, with the scope it belongs to and the detail a reader needs.
/// </summary>
/// <param name="Severity">Which of the five severities below, e.g. <c>path-interference</c>.</param>
/// <param name="Kind">The rule that produced it, e.g. <c>direct-leak</c>.</param>
/// <param name="Scope">What it is about: a <c>pass/row</c>, a <c>pass/row ARM</c>, a pass, or the campaign.</param>
/// <param name="Detail">The finding's own sentence, including the numbers it is about.</param>
internal sealed record Finding(string Severity, string Kind, string Scope, string Detail);

/// <summary>
/// The five severities, in the order they are printed and counted.
/// </summary>
internal static class Severity
{
    /// <summary>The campaign's answer is wrong rather than slow.</summary>
    internal const string CorrectnessFailure = "correctness-failure";

    /// <summary>A product interfered with traffic it was configured to leave alone.</summary>
    internal const string PathInterference = "path-interference";

    /// <summary>The measurement itself is broken.</summary>
    internal const string HarnessError = "harness-error";

    /// <summary>A number is disclosed together with what qualifies it.</summary>
    internal const string MeasurementCaveat = "measurement-caveat";

    /// <summary>A note that records the design rather than a defect.</summary>
    internal const string Informational = "informational";

    /// <summary>Every severity, in the order they are printed and counted.</summary>
    internal static IReadOnlyList<string> Order { get; } =
        [CorrectnessFailure, PathInterference, HarnessError, MeasurementCaveat, Informational];
}

/// <summary>
/// The pre-declared practical-significance thresholds, declared once and printed beside every number they
/// judge.
/// </summary>
internal static class Thresholds
{
    /// <summary>Latency compared as a ratio of medians, against 1 ± this value.</summary>
    internal const double Latency = 0.05;

    /// <summary>UDP loss compared as a difference in percentage points.</summary>
    internal const double UdpLoss = 0.5;
}
