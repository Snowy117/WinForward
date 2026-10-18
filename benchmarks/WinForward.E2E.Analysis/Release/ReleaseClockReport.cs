using System.Globalization;
using WinForward.E2E.Analysis.Findings;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Release;

/// <summary>One ledger read's disagreement count, with the tolerances the ledger findings already use.</summary>
/// <param name="Available">Whether any pass had a ledger.</param>
/// <param name="Files">How many ledger files the campaign held.</param>
/// <param name="Arms">How many arm windows were attributed.</param>
/// <param name="ConnectionMismatches">How many windows disagreed on TCP connections.</param>
/// <param name="DatagramMismatches">How many windows disagreed on datagrams.</param>
/// <param name="Unattributable">How many windows overlapped without a label and could not be assigned.</param>
internal sealed record ReleaseLedgerMismatch(
    bool Available,
    int Files,
    int Arms,
    int ConnectionMismatches,
    int DatagramMismatches,
    int Unattributable);

/// <summary>One pass's clock evidence as the release report prints it.</summary>
/// <param name="PassId">The pass, as the tree spells it.</param>
/// <param name="Role"><c>measured</c> or <c>discarded</c>.</param>
/// <param name="Status">How the pass's offset was derived, or why it was not.</param>
/// <param name="Samples">How many probes the pass contributed.</param>
/// <param name="PreOffsetMs">The pre phase's median offset, or null.</param>
/// <param name="PostOffsetMs">The post phase's median offset, or null.</param>
/// <param name="DriftMs"><c>|post - pre|</c>, or null when one phase is missing.</param>
/// <param name="ResidualMs">The largest probe deviation from the fitted line, or null.</param>
/// <param name="OffsetMs">The offset the correction applied, or null when the pass has no evidence.</param>
/// <param name="DriftExceeded">Whether the pass's drift exceeds the thresholds file's limit.</param>
/// <param name="PreAnchorUtc">The pre median's VM instant, or null.</param>
/// <param name="PostAnchorUtc">The post median's VM instant, or null.</param>
internal sealed record ReleaseClockPassReport(
    string PassId,
    string Role,
    string Status,
    int Samples,
    double? PreOffsetMs,
    double? PostOffsetMs,
    double? DriftMs,
    double? ResidualMs,
    double? OffsetMs,
    bool DriftExceeded,
    DateTimeOffset? PreAnchorUtc,
    DateTimeOffset? PostAnchorUtc);

/// <summary>The release clock section: the log's status, the per-pass evidence and both ledger reads.</summary>
/// <param name="Status">The log's status.</param>
/// <param name="SourcePath">The file the correction was read from.</param>
/// <param name="Samples">How many valid probes the file held.</param>
/// <param name="BadLines">How many lines were not a probe.</param>
/// <param name="Problem">Why the file could not be read, when it could not.</param>
/// <param name="MaxDriftMs">The drift limit the thresholds file declares.</param>
/// <param name="Applied">Whether the ledger attribution was corrected.</param>
/// <param name="Passes">One entry per measured and discarded pass, in pass order.</param>
/// <param name="Corrected">The corrected ledger read, or the uncorrected read when correction was unavailable.</param>
/// <param name="Uncorrected">The uncorrected ledger read.</param>
/// <param name="CorrectedError">Why the corrected read failed, when it did.</param>
/// <param name="UncorrectedError">Why the uncorrected read failed, when it did.</param>
/// <remarks>
/// <b>Absence is not a zero.</b> A campaign without <c>clock.jsonl</c> still gets both ledger reads, but
/// <see cref="Applied"/> is false and the status is <see cref="ClockStatus.Absent"/>, so the verdict can
/// state that the correction was not applied instead of printing an offset of zero. A partial log is
/// reported pass by pass; the passes with one phase are named in their own <see cref="ReleaseClockPassReport.Status"/>.
/// </remarks>
internal sealed record ReleaseClockReport(
    ClockStatus Status,
    string SourcePath,
    int Samples,
    int BadLines,
    string? Problem,
    double MaxDriftMs,
    bool Applied,
    IReadOnlyList<ReleaseClockPassReport> Passes,
    ReleaseLedgerMismatch Corrected,
    ReleaseLedgerMismatch Uncorrected,
    string? CorrectedError,
    string? UncorrectedError)
{
    /// <summary>Reads the campaign's clock log and both ledger attributions.</summary>
    /// <param name="campaignRoot">The campaign root holding <c>clock.jsonl</c> and the passes.</param>
    /// <param name="thresholds">The thresholds file whose clock rule is reported.</param>
    /// <param name="inputs">The pass selection the report describes.</param>
    /// <returns>The clock section, with failures kept as values rather than thrown.</returns>
    internal static ReleaseClockReport Build(string campaignRoot, ThresholdsFile thresholds, ReleaseInputs inputs)
    {
        ArgumentException.ThrowIfNullOrEmpty(campaignRoot);
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(inputs);

        var clock = ClockCorrection.Read(campaignRoot);
        var correction = clock.Corrected ? clock : null;
        var corrected = ReleaseLedger.TryRead(campaignRoot, correction, out var correctedRead, out var correctedError)
            ? correctedRead
            : null;
        var uncorrected = ReleaseLedger.TryRead(campaignRoot, clock: null, out var uncorrectedRead, out var uncorrectedError)
            ? uncorrectedRead
            : null;

        var passes = new List<ReleaseClockPassReport>(inputs.Passes.Measured.Count + inputs.Passes.Discarded.Count);
        var measured = inputs.Passes.Measured.ToHashSet();
        var numbers = inputs.Passes.Measured.Concat(inputs.Passes.Discarded).Distinct().Order().ToList();
        foreach (var number in numbers)
        {
            var passId = "pass" + number.ToString(CultureInfo.InvariantCulture);
            var pass = clock.Pass(passId);
            var role = measured.Contains(number) ? "measured" : "discarded";
            passes.Add(new ReleaseClockPassReport(
                PassId: passId,
                Role: role,
                Status: StatusOf(clock.Status, pass),
                Samples: pass?.Samples ?? 0,
                PreOffsetMs: pass?.PreOffsetMs,
                PostOffsetMs: pass?.PostOffsetMs,
                DriftMs: pass?.DriftMs,
                ResidualMs: pass?.ResidualMs,
                OffsetMs: pass?.OffsetMs,
                DriftExceeded: pass?.DriftMs is { } drift && drift > thresholds.Clock.MaxDriftMs,
                PreAnchorUtc: pass?.PreAnchorUtc,
                PostAnchorUtc: pass?.PostAnchorUtc));
        }

        return new ReleaseClockReport(
            Status: clock.Status,
            SourcePath: clock.SourcePath,
            Samples: clock.Samples,
            BadLines: clock.BadLines,
            Problem: clock.Problem,
            MaxDriftMs: thresholds.Clock.MaxDriftMs,
            Applied: clock.Corrected,
            Passes: passes,
            Corrected: Count(corrected),
            Uncorrected: Count(uncorrected),
            CorrectedError: correctedError,
            UncorrectedError: uncorrectedError);
    }

    /// <summary>The clock section as a <c>release-verdict/1</c> member value.</summary>
    /// <returns>The rendered JSON object.</returns>
    internal string ToJson()
    {
        var passes = new List<(string Key, string Value)>(Passes.Count);
        foreach (var pass in Passes)
        {
            passes.Add((pass.PassId, VerbatimJson.Object(
                3,
                ("role", VerbatimJson.String(pass.Role)),
                ("status", VerbatimJson.String(pass.Status)),
                ("samples", VerbatimJson.Integer(pass.Samples)),
                ("preOffsetMs", Number(pass.PreOffsetMs)),
                ("postOffsetMs", Number(pass.PostOffsetMs)),
                ("driftMs", Number(pass.DriftMs)),
                ("residualMs", Number(pass.ResidualMs)),
                ("offsetMs", Number(pass.OffsetMs)),
                ("driftExceeded", VerbatimJson.Boolean(pass.DriftExceeded)),
                ("preAnchorUtc", Text(pass.PreAnchorUtc)),
                ("postAnchorUtc", Text(pass.PostAnchorUtc)))));
        }

        return VerbatimJson.Object(
            1,
            ("status", VerbatimJson.String(StatusText(Status))),
            ("sourcePath", VerbatimJson.String(SourcePath)),
            ("samples", VerbatimJson.Integer(Samples)),
            ("badLines", VerbatimJson.Integer(BadLines)),
            ("problem", Text(Problem)),
            ("applied", VerbatimJson.Boolean(Applied)),
            ("maxDriftMs", VerbatimJson.Number(MaxDriftMs)),
            ("correctedLedger", Ledger(Corrected, CorrectedError)),
            ("uncorrectedLedger", Ledger(Uncorrected, UncorrectedError)),
            ("passes", VerbatimJson.Object(2, [.. passes])));
    }

    /// <summary>The status name the verdict and the table print.</summary>
    /// <param name="status">The log's status.</param>
    /// <returns>The lower-case name.</returns>
    internal static string StatusText(ClockStatus status) => status switch
    {
        ClockStatus.Absent => "absent",
        ClockStatus.Incomplete => "incomplete",
        ClockStatus.Partial => "partial",
        _ => "complete",
    };

    private static ReleaseLedgerMismatch Count(ReleaseLedgerAttribution? attribution)
    {
        if (attribution is null)
        {
            return new ReleaseLedgerMismatch(
                Available: false,
                Files: 0,
                Arms: 0,
                ConnectionMismatches: 0,
                DatagramMismatches: 0,
                Unattributable: 0);
        }

        var connections = 0;
        var datagrams = 0;
        var unattributable = 0;
        foreach (var arm in attribution.Arms)
        {
            if (arm.Unattributable)
            {
                unattributable++;
            }

            if (arm.ClientConnections is > 0
                && DescriptiveStats.WithinTolerance(
                    arm.TcpConnections,
                    arm.ClientConnections,
                    LedgerFindings.ConnectionTolerance,
                    LedgerFindings.ConnectionSlack) is false)
            {
                connections++;
            }

            if (arm.ClientDatagrams is > 0)
            {
                var band = LedgerFindings.DatagramBand(arm.ClientDatagrams.Value, arm.DurationSeconds);
                if (Math.Abs(arm.UdpDatagrams - arm.ClientDatagrams.Value) > band)
                {
                    datagrams++;
                }
            }
        }

        return new ReleaseLedgerMismatch(
            Available: attribution.Available,
            Files: attribution.LedgerFiles,
            Arms: attribution.Arms.Count,
            ConnectionMismatches: connections,
            DatagramMismatches: datagrams,
            Unattributable: unattributable);
    }

    private static string StatusOf(ClockStatus status, ClockPass? pass)
    {
        if (status is ClockStatus.Absent or ClockStatus.Incomplete)
        {
            return "uncorrected";
        }

        return pass?.Kind switch
        {
            ClockPhaseKind.Interpolated => "interpolated",
            ClockPhaseKind.PreOnly => "pre-only",
            ClockPhaseKind.PostOnly => "post-only",
            _ => "missing",
        };
    }

    private static string Ledger(ReleaseLedgerMismatch mismatch, string? error) => VerbatimJson.Object(
        2,
        ("available", VerbatimJson.Boolean(mismatch.Available)),
        ("files", VerbatimJson.Integer(mismatch.Files)),
        ("arms", VerbatimJson.Integer(mismatch.Arms)),
        ("connectionMismatches", VerbatimJson.Integer(mismatch.ConnectionMismatches)),
        ("datagramMismatches", VerbatimJson.Integer(mismatch.DatagramMismatches)),
        ("unattributable", VerbatimJson.Integer(mismatch.Unattributable)),
        ("error", Text(error)));

    private static string Number(double? value) => value is { } number ? VerbatimJson.Number(number) : VerbatimJson.Null;

    private static string Text(string? value) => value is null ? VerbatimJson.Null : VerbatimJson.String(value);

    private static string Text(DateTimeOffset? value) =>
        value is { } stamp ? VerbatimJson.String(stamp.ToString("O", CultureInfo.InvariantCulture)) : VerbatimJson.Null;
}
