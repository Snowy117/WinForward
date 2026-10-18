using System.Globalization;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Contracts;

namespace WinForward.E2E.Analysis.Findings;

/// <summary>
/// The rules that read the campaign's own failure evidence: a run its <c>run.json</c> failed, a row the
/// pass declared but the tree does not hold, a clean-slate refusal, a campaign-level failure record, and
/// a recorded product order its seed does not reproduce.
/// </summary>
/// <remarks>
/// <para><b>A failed row is a gap, not a zero.</b> A skeleton's empty roster and its recorded stage are
/// reported here so no aggregate can read the row as a measured zero; a run that failed after writing
/// arms keeps its records and is disclosed instead.</para>
/// <para><b>The order checks only run when a pass recorded its seed.</b> A campaign without
/// <c>order.json</c> is read exactly as before: the order is printed from <c>order.txt</c> and nothing is
/// replayed.</para>
/// </remarks>
internal static partial class FindingsCollector
{
    /// <summary>The pass-directory prefix the orchestrator writes.</summary>
    private const string PassPrefix = "pass";

    private static List<Finding> CampaignFailures(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        findings.AddRange(FailedRuns(campaign));
        findings.AddRange(FailureRecords(campaign));
        findings.AddRange(DeclaredRowsMissing(campaign));
        findings.AddRange(OrderIntegrity(campaign));
        return findings;
    }

    private static List<Finding> FailedRuns(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        foreach (var passId in campaign.PassIds)
        {
            foreach (var run in campaign.InPass(passId))
            {
                if (run.Document is not { } document || !JsonValue.IsTrue(document, ArmKeys.Run.Failed))
                {
                    continue;
                }

                var stage = JsonValue.String(document, ArmKeys.Run.Stage);
                var error = JsonValue.String(document, ArmKeys.Run.Error);
                var what = string.IsNullOrEmpty(stage) && string.IsNullOrEmpty(error)
                    ? "run.json says failed=true"
                    : $"run.json says failed=true at stage '{stage}': {error}";
                var consequence = run.Arms.Count == 0
                    ? "the row left no arm records, so every cell for it is a gap rather than a measurement"
                    : "the arm records it wrote are kept and every cell that carries them is disclosed";
                findings.Add(new Finding(
                    Severity.HarnessError,
                    "failed-row",
                    $"{passId}/{run.RunId}",
                    $"{what}; {consequence}"));
            }
        }

        return findings;
    }

    private static List<Finding> FailureLogFindings(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        if (campaign.FailureLogError is { } logError)
        {
            findings.Add(new Finding(
                Severity.MeasurementCaveat,
                "failure-log-unreadable",
                "campaign",
                $"{logError}; the failure record is reported as missing rather than read as an empty one"));
        }

        if (campaign.FailureLogBadLines > 0)
        {
            findings.Add(new Finding(
                Severity.MeasurementCaveat,
                "failure-log-unreadable",
                "campaign",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{campaign.FailureLogBadLines} line(s) of the failure record are not records; a failure may be missing from the list")));
        }

        return findings;
    }

    private static List<Finding> FailureRecords(CampaignModel campaign)
    {
        var findings = FailureLogFindings(campaign);
        foreach (var failure in campaign.Failures)
        {
            var scope = ScopeOf(campaign, failure);
            if (failure.CleanSlate)
            {
                var consequence = string.IsNullOrEmpty(failure.Row)
                    ? "the pass was refused rather than measured on an unclean machine"
                    : "the row was skipped rather than measured on an unclean machine";
                findings.Add(new Finding(
                    Severity.HarnessError,
                    "clean-slate-failure",
                    scope,
                    $"the {failure.Stage} step failed: {failure.Error}; {consequence}"
                    + DetailSuffix(failure)));
                continue;
            }

            if (string.Equals(failure.Stage, "preflight", StringComparison.Ordinal)
                || string.Equals(failure.Stage, "target", StringComparison.Ordinal)
                || string.Equals(failure.Stage, "teardown", StringComparison.Ordinal))
            {
                findings.Add(new Finding(
                    Severity.HarnessError,
                    "campaign-failure",
                    scope,
                    $"the {failure.Stage} stage failed: {failure.Error}" + DetailSuffix(failure)));
                continue;
            }

            if (RecordNamesAnUndisclosedFailure(campaign, failure))
            {
                findings.Add(new Finding(
                    Severity.HarnessError,
                    "campaign-failure",
                    scope,
                    $"{failure.Stage}: {failure.Error}; the run's own run.json does not report it" + DetailSuffix(failure)));
            }
        }

        return findings;
    }

    /// <summary>
    /// Whether a row-stage record names something no failed <c>run.json</c> discloses. A record whose row
    /// does have a failed run, or has no run at all, is owned by <c>failed-row</c> or
    /// <c>declared-row-missing</c> and is not repeated here.
    /// </summary>
    private static bool RecordNamesAnUndisclosedFailure(CampaignModel campaign, CampaignFailure failure)
    {
        if (string.IsNullOrEmpty(failure.Row))
        {
            return false;
        }

        // A row-stage record names one pass; the same row failing in another pass must not suppress it.
        var passIds = failure.Pass is { } number
            ? campaign.PassIds.Where(passId => PassNumber(passId) == number)
            : campaign.PassIds;
        var runs = passIds
            .SelectMany(campaign.InPass)
            .Where(run => string.Equals(run.RunId, failure.Row, StringComparison.Ordinal))
            .ToList();
        if (runs.Count == 0)
        {
            return false;
        }

        return !runs.Exists(run => run.Document is { } document && JsonValue.IsTrue(document, ArmKeys.Run.Failed));
    }
    private static List<Finding> DeclaredRowsMissing(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        if (campaign.Flat)
        {
            return findings;
        }

        foreach (var passId in campaign.PassIds)
        {
            var declared = campaign.Order.TryGetValue(passId, out var ran) && ran.Count > 0 ? ran : RowProfiles.Order;
            var present = campaign.InPass(passId).Select(run => run.RunId).ToHashSet(StringComparer.Ordinal);
            var number = PassNumber(passId);
            foreach (var rowId in declared.Where(rowId => !present.Contains(rowId)))
            {
                var recorded = campaign.Failures.FirstOrDefault(failure =>
                    RowMatches(failure, rowId)
                    && (number is null || failure.Pass is null || failure.Pass == number));
                findings.Add(new Finding(
                    Severity.HarnessError,
                    "declared-row-missing",
                    $"{passId}/{rowId}",
                    $"the pass declares {rowId} but the tree holds no run.json or arm file for it"
                    + (recorded is null ? string.Empty : $"; the recorded failure says: {recorded.Error}")
                    + "; a missing row is reported rather than read as a zero"));
            }
        }

        return findings;
    }

    private static List<Finding> OrderUnreadable(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        foreach (var (passId, error) in campaign.OrderRecordErrors.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            findings.Add(new Finding(
                Severity.MeasurementCaveat,
                "order-unreadable",
                passId,
                $"{error}; the permutation is not replayed for this pass and the order is taken from order.txt as recorded"));
        }

        return findings;
    }

    private static List<Finding> OrderIntegrity(CampaignModel campaign)
    {
        var findings = OrderUnreadable(campaign);
        var products = RowProfiles.ProductOrder;
        foreach (var passId in campaign.PassIds)
        {
            if (campaign.OrderRecords.TryGetValue(passId, out var record))
            {
                findings.AddRange(OrderRecordFindings(campaign, passId, record, products));
            }
        }

        return findings;
    }

    private static List<Finding> OrderRecordFindings(
        CampaignModel campaign,
        string passId,
        OrderRecord record,
        IReadOnlyList<string> products)
    {
        var findings = new List<Finding>();
        if (record.Algorithm is { } algorithm && !string.Equals(algorithm, OrderPermutation.Algorithm, StringComparison.Ordinal))
        {
            findings.Add(new Finding(
                Severity.MeasurementCaveat,
                "order-algorithm-unknown",
                passId,
                $"the recorded permutation names generator '{algorithm}', which this analysis cannot replay; the order is taken from order.txt as recorded"));
            return findings;
        }

        if (!RecordedRowsAreDeclaredProducts(record.Rows, products))
        {
            findings.Add(new Finding(
                Severity.HarnessError,
                "order-rows-mismatch",
                passId,
                $"order.json names {string.Join(", ", record.Rows)}, which is not a subset of the declared product rows {string.Join(", ", products)}"));
            return findings;
        }

        // A restricted smoke run shuffles only the rows it was asked for, so the pre-shuffle order is
        // the recorded set read in declaration order -- which is the whole product table for a campaign.
        var shuffled = products.Where(rowId => record.Rows.Contains(rowId, StringComparer.Ordinal)).ToList();
        var replay = OrderPermutation.Apply(record.Seed, shuffled);
        if (!replay.SequenceEqual(record.Rows, StringComparer.Ordinal))
        {
            findings.Add(new Finding(
                Severity.HarnessError,
                "order-unreproducible",
                passId,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"seed {record.Seed} produces {string.Join(", ", replay)}, not the recorded {string.Join(", ", record.Rows)}; the permutation did not come from the declared generator")));
        }

        if (campaign.Order.TryGetValue(passId, out var text) && text.Count > 0)
        {
            var recorded = text.Where(rowId => products.Contains(rowId, StringComparer.Ordinal)).ToList();
            if (!recorded.SequenceEqual(record.Rows, StringComparer.Ordinal))
            {
                findings.Add(new Finding(
                    Severity.HarnessError,
                    "order-mismatch",
                    passId,
                    $"order.txt records {string.Join(", ", recorded)} but order.json records {string.Join(", ", record.Rows)}; the two records of one permutation disagree"));
            }
        }

        return findings;
    }

    private static bool RecordedRowsAreDeclaredProducts(IReadOnlyList<string> rows, IReadOnlyList<string> products) =>
        rows.All(rowId => products.Contains(rowId, StringComparer.Ordinal))
        && new HashSet<string>(rows, StringComparer.Ordinal).Count == rows.Count;

    private static bool RowMatches(CampaignFailure failure, string rowId) =>
        string.Equals(failure.Row, rowId, StringComparison.Ordinal);

    private static string ScopeOf(CampaignModel campaign, CampaignFailure failure)
    {
        var passId = failure.Pass is { } number
            ? campaign.PassIds.FirstOrDefault(id => PassNumber(id) == number)
            : null;
        if (string.IsNullOrEmpty(passId))
        {
            passId = failure.Pass is { } unparsed
                ? PassPrefix + unparsed.ToString(CultureInfo.InvariantCulture)
                : "campaign";
        }

        return string.IsNullOrEmpty(failure.Row) ? passId : $"{passId}/{failure.Row}";
    }

    private static string DetailSuffix(CampaignFailure failure) =>
        string.IsNullOrEmpty(failure.Detail) ? string.Empty : $" ({failure.Detail})";

    private static int? PassNumber(string passId) =>
        passId.StartsWith(PassPrefix, StringComparison.Ordinal)
        && int.TryParse(passId.AsSpan(PassPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
}
