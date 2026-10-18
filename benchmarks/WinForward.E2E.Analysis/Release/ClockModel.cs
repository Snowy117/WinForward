namespace WinForward.E2E.Analysis.Release;

/// <summary>
/// What a campaign's <c>clock.jsonl</c> supports: nothing (absent), a file with no usable probe
/// (incomplete), some passes with only one phase (partial), or both phases for every probed pass
/// (complete).
/// </summary>
/// <remarks>
/// <b>The status describes the file's evidence, not the campaign tree.</b> A complete log can still omit
/// a pass the tree ran, which <see cref="ClockCorrection.Pass"/> reports as missing for that pass; the
/// release verdict is what joins the two. The names are the ones a release report has to
/// state, so a reader of that document can map them one-to-one.
/// </remarks>
public enum ClockStatus
{
    /// <summary>No <c>clock.jsonl</c> at the campaign root, so no window is corrected.</summary>
    Absent,

    /// <summary>The file exists but holds no usable probe, so no window is corrected.</summary>
    Incomplete,

    /// <summary>Usable probes, but at least one probed pass is missing a phase; that pass uses the phase it has.</summary>
    Partial,

    /// <summary>Every probed pass carries a pre and a post phase.</summary>
    Complete,
}

/// <summary>How one pass's offset was derived from its probes.</summary>
public enum ClockPhaseKind
{
    /// <summary>Both phases are present; the pass offset is read off the pre-to-post drift line.</summary>
    Interpolated,

    /// <summary>Only the pre phase is present; its median is the pass offset.</summary>
    PreOnly,

    /// <summary>Only the post phase is present; its median is the pass offset.</summary>
    PostOnly,

    /// <summary>The file carries no probe for this pass; the pass keeps its VM window.</summary>
    Missing,
}

/// <summary>
/// One pass's clock evidence: the two phase medians, the drift between them, the probe scatter around
/// that line (or around the single phase's median when only one phase is present), and the offset the
/// correction applies to the pass's windows.
/// </summary>
/// <remarks>
/// <b>The residual is measured against the line, not against the medians.</b> It is the probe's own
/// noise as a release report prints it, so a probe that sits far from the interpolation at its own
/// instant counts even when the medians themselves are tight.
/// </remarks>
public sealed record ClockPass
{
    /// <summary>The pass as the tree spells it, <c>pass&lt;N&gt;</c>.</summary>
    public required string PassId { get; init; }

    /// <summary>The pass number the log declared.</summary>
    public required int Pass { get; init; }

    /// <summary>Which of the two phases the offset was derived from.</summary>
    public required ClockPhaseKind Kind { get; init; }

    /// <summary>How many probes the pass contributed.</summary>
    public required int Samples { get; init; }

    /// <summary>The pre phase's median offset, or null when the phase is absent.</summary>
    public double? PreOffsetMs { get; init; }

    /// <summary>The post phase's median offset, or null when the phase is absent.</summary>
    public double? PostOffsetMs { get; init; }

    /// <summary><c>|post - pre|</c> when both phases are present, otherwise null.</summary>
    public required double? DriftMs { get; init; }

    /// <summary>
    /// The largest absolute deviation of the pass's probes from the pre/post line; a one-phase pass has no
    /// line, so its probes are read against that phase's own median.
    /// </summary>
    public required double? ResidualMs { get; init; }

    /// <summary>The offset this pass's windows are translated by, or null when the pass is missing.</summary>
    public required double? OffsetMs { get; init; }

    /// <summary>The VM instant the pre median sits at, or null when the phase is absent.</summary>
    public DateTimeOffset? PreAnchorUtc { get; init; }

    /// <summary>The VM instant the post median sits at, or null when the phase is absent.</summary>
    public DateTimeOffset? PostAnchorUtc { get; init; }
}

/// <summary>
/// One arm window as the ledger attribution reads it: the run's own VM window, the offset the clock
/// correction derived for its pass, and the resulting host window.
/// </summary>
/// <remarks>
/// <b>The translation is one offset for the whole window.</b> A correction that moved the two edges by
/// different amounts would rescale a duration the client measured with a monotonic stopwatch; the
/// drift line is evaluated once per window and subtracted, and the line's own slope is reported as the
/// pass drift instead.
/// </remarks>
public sealed record CorrectedWindow
{
    /// <summary>The pass the window belongs to.</summary>
    public required string PassId { get; init; }

    /// <summary>The window's start on the client's clock.</summary>
    public required DateTimeOffset VmStart { get; init; }

    /// <summary>The window's end on the client's clock.</summary>
    public required DateTimeOffset VmEnd { get; init; }

    /// <summary>The window's start on the ledger's clock.</summary>
    public required DateTimeOffset HostStart { get; init; }

    /// <summary>The window's end on the ledger's clock.</summary>
    public required DateTimeOffset HostEnd { get; init; }

    /// <summary>The signed offset subtracted from the VM window; zero when nothing was corrected.</summary>
    public required double OffsetMs { get; init; }

    /// <summary>How the offset was derived.</summary>
    public required ClockPhaseKind Kind { get; init; }

    /// <summary>Whether this window was moved by a clock offset rather than left in VM time.</summary>
    public bool Corrected => Kind is not ClockPhaseKind.Missing;

    /// <summary>How far the host window sits from the VM window; zero when nothing was corrected.</summary>
    public TimeSpan Shift => HostStart - VmStart;
}
