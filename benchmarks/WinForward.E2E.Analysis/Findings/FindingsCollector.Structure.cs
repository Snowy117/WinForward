using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Analysis.Checks;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Findings;

/// <summary>
/// The rules that read the campaign's own structure rather than a single arm's records: an arm the plan
/// declares that is not on disk, the DNS arms' ports, the control blocks that bracket the product block,
/// and the design notes that say which rows measure which path.
/// </summary>
internal static partial class FindingsCollector
{
    private static List<Finding> Arms(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        foreach (var passId in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(passId))
            {
                var declared = RowProfiles.Planned(row.RunId);
                if (declared is null)
                {
                    continue;
                }

                var observed = row.Arms.SortedNames;
                var missing = declared.Where(name => !row.Arms.Contains(name)).ToList();
                var extra = observed.Where(name => !declared.Contains(name, StringComparer.Ordinal)).ToList();
                var plan = RowProfiles.Find(row.RunId)?.Plan ?? string.Empty;
                if (missing.Count > 0)
                {
                    findings.Add(new Finding(
                        Severity.MeasurementCaveat,
                        "declared-arm-missing",
                        $"{passId}/{row.RunId}",
                        $"the {plan} plan declares {string.Join(", ", declared)} but no file was found for: {string.Join(", ", missing)}"));
                }

                if (extra.Count > 0)
                {
                    findings.Add(new Finding(
                        Severity.MeasurementCaveat,
                        "undeclared-arm-present",
                        $"{passId}/{row.RunId}",
                        $"the {plan} plan does not declare {string.Join(", ", extra)}; the data is still reported, "
                        + "and the plan table is what the comparability rule trusts"));
                }
            }
        }

        return findings;
    }

    private static List<Finding> DnsPorts(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        foreach (var passId in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(passId))
            {
                var (altPort, altWhy) = ArmAccess.Text(row, "DNSALT", "parameters/dnsPort");
                if (altPort is null)
                {
                    continue;
                }

                findings.AddRange(DnsPortFindings(passId, row, altPort, altWhy));
            }
        }

        return findings;
    }

    private static List<Finding> DnsPortFindings(string passId, ClientRun row, JsonElement? altPort, string? altWhy)
    {
        var findings = new List<Finding>();
        DnsAltCheck(findings, passId, row, altPort, altWhy);
        DnsPortCheck(findings, passId, row, altPort);
        return findings;
    }

    private static void DnsAltCheck(List<Finding> findings, string passId, ClientRun row, JsonElement? altPort, string? altWhy)
    {
        if (JsonValue.AsNumber(altPort) is not 53.0)
        {
            return;
        }

        findings.Add(new Finding(
            Severity.HarnessError,
            "dnsalt-on-53",
            $"{passId}/{row.RunId} DNSALT",
            "the DNSALT arm ran on port 53, which is the port products special-case, so it is not the "
            + $"comparable arm ({altWhy ?? "port 53"})"));
    }

    private static void DnsPortCheck(List<Finding> findings, string passId, ClientRun row, JsonElement? altPort)
    {
        var (dnsPort, _) = ArmAccess.Text(row, "DNS", "parameters/dnsPort");
        if (dnsPort is not null && JsonText.Same(dnsPort, altPort))
        {
            findings.Add(new Finding(
                Severity.HarnessError,
                "dns-port-collision",
                $"{passId}/{row.RunId}",
                $"the DNS and DNSALT arms both ran on port {JsonText.Of(dnsPort)}, so the two arms measure the same port"));
        }

        if (JsonValue.AsNumber(dnsPort) is not null and not 53.0)
        {
            findings.Add(new Finding(
                Severity.Informational,
                "dns-arm-port",
                $"{passId}/{row.RunId} DNS",
                $"the port-53 arm ran on port {JsonText.Of(dnsPort)}, not 53: the products' hardcoded port-53 and "
                + "local-target special cases are declared for port 53, so the carriage label for this arm is "
                + "reported as declared but the port is stated"));
        }
    }

    private static List<Finding> ControlBlocks(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        foreach (var passId in campaign.PassIds)
        {
            var pre = campaign.InPass(passId, RowProfiles.ControlPre);
            var post = campaign.InPass(passId, RowProfiles.ControlPost);
            if (pre is null || post is null)
            {
                string what;
                if (pre is not null)
                {
                    what = $"only {RowProfiles.ControlPre}";
                }
                else
                {
                    what = post is not null ? $"only {RowProfiles.ControlPost}" : "neither control block";
                }

                findings.Add(new Finding(
                    Severity.MeasurementCaveat,
                    "control-block-missing",
                    passId,
                    $"the pass has {what}; both control blocks are needed to bracket the product block"));
                continue;
            }

            var ordering = ControlDrift.Ordering(campaign, passId);
            if (ordering is not null)
            {
                findings.Add(new Finding(Severity.CorrectnessFailure, "control-bracketing", passId, ordering));
            }
        }

        findings.AddRange(DriftFindings(campaign));
        return findings;
    }

    private static List<Finding> DriftFindings(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        foreach (var entry in ControlDrift.Compute(campaign).Comparisons)
        {
            if (entry.Comparison is null)
            {
                continue;
            }

            var scope = string.Create(
                CultureInfo.InvariantCulture,
                $"{RowProfiles.ControlPost} vs {RowProfiles.ControlPre} across {entry.PassesUsed} pass(es): {entry.Metric}");
            if (string.Equals(entry.Verdict, "different", StringComparison.Ordinal))
            {
                findings.Add(new Finding(
                    Severity.CorrectnessFailure,
                    "control-drift",
                    scope,
                    $"{entry.Statement}; the post block is the only thing in the campaign that can detect a product that left a driver filtering after it exited"));
            }
            else if (string.Equals(entry.Verdict, "inconclusive", StringComparison.Ordinal))
            {
                findings.Add(new Finding(
                    Severity.MeasurementCaveat,
                    "control-drift-undecided",
                    scope,
                    $"{entry.Statement}: the two control blocks are not shown to agree"));
            }
        }

        return findings;
    }

    private static List<Finding> Design(CampaignModel campaign)
    {
        var findings = new List<Finding>();
        foreach (var rowId in campaign.RowIds)
        {
            var profile = RowProfiles.Find(rowId);
            if (profile is null || string.Equals(profile.Udp53, RowProfiles.Udp53NotApplicable, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(profile.Udp53, RowProfiles.Udp53Relayed, StringComparison.Ordinal))
            {
                findings.Add(new Finding(
                    Severity.Informational,
                    "udp53-direct",
                    rowId,
                    $"the port-53 DNS arm is a direct-path measurement on this row ({RowProfiles.Udp53Label(profile.Udp53)}); "
                    + "the cross-product DNS comparison runs on DNSALT"));
            }

            if (string.Equals(profile.Udp, RowProfiles.UdpNotCarried, StringComparison.Ordinal))
            {
                findings.Add(new Finding(
                    Severity.Informational,
                    "udp-not-carried",
                    rowId,
                    $"every UDP cell for this row reads '{RowProfiles.NotCarriedCell}' and the row is excluded from the "
                    + "UDP-accuracy and DNS-latency comparisons; its TCP results are unaffected"));
            }
        }

        return findings;
    }
}
