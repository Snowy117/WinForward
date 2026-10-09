using System.Globalization;
using WinForward.E2E.Analysis.Checks;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Findings;

/// <summary>
/// Every correctness failure, harness error, measurement caveat and informational note the campaign
/// produces, in the reference's own order, each one once.
/// </summary>
/// <remarks>
/// <para><b>The order is output.</b> The report lists the findings by severity in the order they
/// were collected, and <c>verdict.json</c> publishes the whole list in that order, so a rule
/// evaluated earlier cannot be reordered later.</para>
/// <para><b>A finding is a rule, a scope and a sentence.</b> The sentence carries the numbers it is about
/// because a reader must be able to check it without re-running the analysis.</para>
/// </remarks>
internal static partial class FindingsCollector
{
    /// <summary>Every finding the campaign produces, deduplicated, in collection order.</summary>
    internal static List<Finding> Collect(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var collected = new List<Finding>();
        collected.AddRange(DualTruth(campaign));
        collected.AddRange(ForeignConnections(campaign));
        collected.AddRange(Identities(campaign));
        collected.AddRange(LaneWitnesses(campaign));
        collected.AddRange(LatencyGates(campaign));
        collected.AddRange(Sampling(campaign));
        collected.AddRange(Arms(campaign));
        collected.AddRange(DnsPorts(campaign));
        collected.AddRange(ControlBlocks(campaign));
        collected.AddRange(LedgerFindings.Collect(campaign));
        collected.AddRange(Design(campaign));
        collected.AddRange(DualFindings.Collect(campaign));
        return Deduplicate(collected);
    }

    /// <summary>The findings each severity holds, in the order they were collected.</summary>
    internal static Dictionary<string, List<Finding>> BySeverity(IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var bySeverity = new Dictionary<string, List<Finding>>(StringComparer.Ordinal);
        foreach (var severity in Severity.Order)
        {
            bySeverity[severity] = [];
        }

        foreach (var finding in findings)
        {
            if (!bySeverity.TryGetValue(finding.Severity, out var entries))
            {
                entries = [];
                bySeverity[finding.Severity] = entries;
            }

            entries.Add(finding);
        }

        return bySeverity;
    }

    private static List<Finding> DualTruth(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        foreach (var passId in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(passId))
            {
                var profile = RowProfiles.Find(row.RunId);
                if (row.DualTruth is null)
                {
                    if (profile is { Dual: true })
                    {
                        findings.Add(new Finding(
                            Severity.MeasurementCaveat,
                            "dual-truth-missing",
                            $"{passId}/{row.RunId}",
                            "the row's plan declares a dual phase but dual/proxy-truth.json is missing, so directLeak cannot be checked"));
                    }

                    continue;
                }

                switch (JsonValue.Number(row.DualTruth, "directLeak"))
                {
                    case null:
                        findings.Add(new Finding(
                            Severity.MeasurementCaveat,
                            "direct-leak-unreadable",
                            $"{passId}/{row.RunId}",
                            "dual/proxy-truth.json has no numeric directLeak field"));
                        break;
                    case > 0.0:
                        var leak = JsonValue.Number(row.DualTruth, "directLeak")!.Value;
                        findings.Add(new Finding(
                            Severity.CorrectnessFailure,
                            "direct-leak",
                            $"{passId}/{row.RunId} (dual/direct)",
                            $"directLeak={JsonNumber.IntText(leak)}: the product intercepted {JsonNumber.IntText(leak)} application connection(s) it was configured to send direct"));
                        break;
                }

                foreach (var (lane, present) in new[] { ("proxied", row.DualProxied), ("direct", row.DualDirect) })
                {
                    if (present is null)
                    {
                        findings.Add(new Finding(
                            Severity.MeasurementCaveat,
                            "dual-lane-missing",
                            $"{passId}/{row.RunId}",
                            $"dual/{lane} run directory is missing"));
                    }
                }
            }
        }

        return findings;
    }

    private static List<Finding> ForeignConnections(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        foreach (var passId in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(passId))
            {
                foreach (var (armName, label, count) in IdentityChecks.ForeignConnectionSources(row))
                {
                    findings.Add(new Finding(
                        Severity.CorrectnessFailure,
                        "foreign-connection",
                        $"{passId}/{row.RunId} {armName}",
                        $"{label}={VerbatimNumber.Cell(count, 0)}: the product delivered {VerbatimNumber.Cell(count, 0)} datagram(s) belonging to one flow into a different flow"));
                }
            }
        }

        return findings;
    }

    private static List<Finding> Identities(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        foreach (var passId in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(passId))
            {
                foreach (var check in IdentityChecks.Checks(row))
                {
                    switch (check.Ok)
                    {
                        case false when string.Equals(check.Kind, "scheduled-attempts", StringComparison.Ordinal):
                            findings.Add(new Finding(
                                Severity.MeasurementCaveat,
                                check.Kind,
                                $"{passId}/{row.RunId} {check.Arm}",
                                $"{check.Detail}; the values are kept but every cell that carries them says so"));
                            break;
                        case false:
                            findings.Add(new Finding(
                                Severity.HarnessError,
                                check.Kind,
                                $"{passId}/{row.RunId} {check.Arm}",
                                $"{check.Detail}; the record is excluded from the affected aggregates rather than averaged over"));
                            break;
                        case null:
                            findings.Add(new Finding(
                                Severity.MeasurementCaveat,
                                check.Kind + "-uncheckable",
                                $"{passId}/{row.RunId} {check.Arm}",
                                check.Detail ?? string.Empty));
                            break;
                    }
                }
            }
        }

        return findings;
    }

    private static List<Finding> LaneWitnesses(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        foreach (var passId in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(passId))
            {
                foreach (var armName in ArmRecords.LoadOrder)
                {
                    var witnesses = IdentityChecks.LaneWitnesses(row, armName);
                    if (witnesses is null)
                    {
                        continue;
                    }

                    foreach (var (label, ok) in witnesses)
                    {
                        if (!ok)
                        {
                            findings.Add(new Finding(
                                Severity.HarnessError,
                                "lane-witness-zero",
                                $"{passId}/{row.RunId} {armName}",
                                $"{label}: the witness is zero, so that lane never ran and the arm measured less than its plan"));
                        }
                    }
                }

                var idle = ArmAccess.Number(row, "MIX", "gates/idleLanes").Value;
                if (idle is not null and not 0.0)
                {
                    findings.Add(new Finding(
                        Severity.HarnessError,
                        "idle-lanes",
                        $"{passId}/{row.RunId} MIX",
                        $"gates.idleLanes={VerbatimNumber.Cell(idle, 0)}: {VerbatimNumber.Cell(idle, 0)} per-desktop flow-class witness(es) stayed at zero"));
                }
            }
        }

        return findings;
    }

    private static List<Finding> LatencyGates(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        foreach (var passId in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(passId))
            {
                foreach (var armName in new[] { "LAT", "LATLOAD", "BASE" })
                {
                    var (result, _) = ArmAccess.ArmResult(row, armName);
                    if (result is null)
                    {
                        continue;
                    }

                    var backlog = ArmAccess.Number(row, armName, "gates/backlogDrops").Value;
                    var truncated = ArmAccess.Number(row, armName, "gates/scheduleTruncated").Value;
                    var overflow = ArmAccess.Number(row, armName, "gates/windowOverflow").Value;
                    var ceiling = ArmAccess.Number(row, armName, "gates/inFlightCeilingMs").Value;
                    if (backlog is not null and not 0.0)
                    {
                        findings.Add(new Finding(
                            Severity.HarnessError,
                            "latency-backlog-drops",
                            $"{passId}/{row.RunId} {armName}",
                            $"gates.backlogDrops={VerbatimNumber.Cell(backlog, 0)}: the arm's own deferred queue discarded {VerbatimNumber.Cell(backlog, 0)} request(s), so its tail is censored"));
                    }

                    if (truncated is not null and not 0.0)
                    {
                        findings.Add(new Finding(
                            Severity.HarnessError,
                            "latency-schedule-truncated",
                            $"{passId}/{row.RunId} {armName}",
                            $"gates.scheduleTruncated={VerbatimNumber.Cell(truncated, 0)}: part of the offered schedule was never offered"));
                    }

                    if (overflow is not null and not 0.0)
                    {
                        findings.Add(new Finding(
                            Severity.MeasurementCaveat,
                            "latency-ceiling-reached",
                            $"{passId}/{row.RunId} {armName}",
                            $"gates.windowOverflow={VerbatimNumber.Cell(overflow, 0)} with gates.inFlightCeilingMs="
                            + $"{VerbatimNumber.Cell(ceiling, 1)}: requests past the direct-latency ceiling "
                            + $"{VerbatimNumber.Cell(ceiling, 1)} ms were measured through the deferred queue"));
                    }
                }
            }
        }

        return findings;
    }

    private static List<Finding> Sampling(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        foreach (var passId in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(passId))
            {
                var errors = RunSamples.SamplerErrors(row);
                if (errors.Count > 0)
                {
                    var processes = errors
                        .Select(record => JsonText.Of(JsonValue.Member(record, "process")))
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal);
                    findings.Add(new Finding(
                        Severity.HarnessError,
                        "sampler-error",
                        $"{passId}/{row.RunId}",
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{errors.Count} samplerError record(s) for {string.Join(", ", processes)}: the 1 Hz trace has a sampling gap that is disclosed here, not averaged over")));
                }

                var unreadable = row.Arms.All
                    .SelectMany(arm => arm.Samples)
                    .Where(sample => !RunSamples.IsReadable(sample))
                    .ToList();
                if (unreadable.Count > 0)
                {
                    var readErrors = unreadable.Sum(sample => (long)Math.Truncate(JsonValue.Number(sample, "readErrors") ?? 0.0));
                    findings.Add(new Finding(
                        Severity.HarnessError,
                        "sample-read-error",
                        $"{passId}/{row.RunId}",
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{unreadable.Count} sample(s) carried readError with {readErrors} unreadable process counter(s); those samples are rejected from every CPU and memory cell")));
                }
            }
        }

        return findings;
    }

    private static List<Finding> Deduplicate(List<Finding> findings)
    {
        var seen = new HashSet<(string, string, string, string)>();
        return [.. findings.Where(finding => seen.Add((finding.Severity, finding.Kind, finding.Scope, finding.Detail)))];
    }
}
