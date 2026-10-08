using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Analysis.Checks;
using WinForward.E2E.Analysis.Findings;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// §0, "Correctness findings": the severity counts, the three tables that show a reader what went wrong
/// rather than how fast it went, and the whole finding list grouped by severity.
/// </summary>
/// <remarks>
/// <para><b>This section comes first because its subject is correctness.</b> A non-zero <c>directLeak</c>
/// is not a performance result, a datagram delivered into the wrong flow invalidates the number it
/// displaced, and an accounting identity that does not hold means the record cannot be averaged over.</para>
/// <para><b>A check that could not be run is not a check that passed.</b> The identities have three
/// outcomes and the section prints how many held and how many could not be checked at all.</para>
/// </remarks>
internal static class TableFindings
{
    private const string NoGap = "—";

    /// <summary>The section's body, without its heading and ending in a newline.</summary>
    internal static string RenderBody(CampaignModel campaign, IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(findings);

        var bySeverity = FindingsCollector.BySeverity(findings);
        var lines = new List<string>
        {
            "**" + string.Join(
                " · ",
                Severity.Order.Select(severity => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{bySeverity[severity].Count} {severity}"))) + ".**",
            string.Empty,
            "A **correctness failure** means the campaign's answer is wrong rather than slow — traffic that "
            + "went where it was not configured to go, or a datagram delivered into the wrong flow. A **harness "
            + "error** means the measurement itself is broken: an accounting identity that does not hold, a lane "
            + "that never ran, a sampling gap. A **measurement caveat** is disclosed with the number it qualifies. "
            + "`informational` notes record the design's own direct-path arms so they cannot be mistaken for "
            + "proxied results.",
            string.Empty,
        };

        DirectLeak(campaign, lines);
        ForeignConnections(campaign, lines);
        Identities(campaign, lines);
        AllBySeverity(bySeverity, lines);
        return string.Join('\n', lines);
    }

    private static void DirectLeak(CampaignModel campaign, List<string> lines)
    {
        lines.Add("### 0.1 `directLeak` — traffic the product was configured to send direct");
        lines.Add(string.Empty);
        var rows = new List<IReadOnlyList<string>>();
        foreach (var passId in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(passId))
            {
                if (LeakRow(passId, row) is { } cells)
                {
                    rows.Add(cells);
                }
            }
        }

        lines.Add(rows.Count > 0
            ? MarkdownTable.Render(
                ["pass", "row", "directLeak", "truth tcp", "truth udp", "truth utcp", "lanes", "verdict"],
                rows)
            : "n/a (no row in this tree ran a dual phase)");
        lines.Add(string.Empty);
        lines.Add(
            "A non-zero `directLeak` is a correctness failure, not a performance result: the product "
            + "intercepted an application it was configured to send direct. It is reported here, above every "
            + "performance table, for exactly that reason.");
        lines.Add(string.Empty);
    }

    private static IReadOnlyList<string>? LeakRow(string passId, ClientRun row)
    {
        if (row.DualTruth is null)
        {
            return RowProfiles.Find(row.RunId) is { Dual: true }
                ? [passId, row.RunId, "n/a (no dual/proxy-truth.json)", "n/a", "n/a", "n/a", "n/a", "n/a"]
                : null;
        }

        var leak = JsonValue.Number(row.DualTruth, "directLeak");
        var verdict = leak switch
        {
            null => "n/a (no numeric directLeak in dual/proxy-truth.json)",
            > 0.0 => $"CORRECTNESS FAILURE: the product intercepted {VerbatimNumber.Cell(leak, 0)} direct-path application connection(s)",
            _ => "0 direct-path interceptions observed",
        };

        var lanes = $"{(row.DualProxied is not null ? "present" : "missing")} / {(row.DualDirect is not null ? "present" : "missing")}";
        return
        [
            passId,
            row.RunId,
            VerbatimNumber.Cell(leak, 0),
            VerbatimNumber.Cell(JsonValue.Number(row.DualTruth, "tcp"), 0),
            VerbatimNumber.Cell(JsonValue.Number(row.DualTruth, "udp"), 0),
            VerbatimNumber.Cell(JsonValue.Number(row.DualTruth, "utcp"), 0),
            lanes,
            verdict,
        ];
    }

    private static void ForeignConnections(CampaignModel campaign, List<string> lines)
    {
        lines.Add("### 0.2 `foreignConnection` — datagrams delivered into the wrong flow");
        lines.Add(string.Empty);
        var rows = new List<IReadOnlyList<string>>();
        var readable = 0;
        foreach (var passId in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(passId))
            {
                readable += ForeignRows(passId, row, rows);
            }
        }

        var bound = Math.Max(readable, 1);
        lines.Add(rows.Count > 0
            ? MarkdownTable.Render(["pass", "row", "arm", "counter", "value", "verdict"], rows)
            : $"None observed: {readable.ToString(CultureInfo.InvariantCulture)} readable `foreignConnection` counter(s) across every (pass, row, arm). "
                + $"A zero bounds the true rate near 3/{bound.ToString(CultureInfo.InvariantCulture)} — it does not prove it is zero — and the "
                + "counters are re-read every pass, so a small but non-zero value is reported here on the pass that produced it.");
        lines.Add(string.Empty);
        lines.Add(
            "A reply carrying another flow's connection id validates against that flow's own filler, so without "
            + "this counter it would register as a legitimate arrival and the sequence it displaced would be "
            + "reported as loss. A non-zero value is therefore a correctness finding, never a performance one.");
        lines.Add(string.Empty);
    }

    private static int ForeignRows(string passId, ClientRun row, List<IReadOnlyList<string>> rows)
    {
        var readable = 0;
        foreach (var (armName, path, label) in new[]
        {
            ("LOSS", "metrics/foreignConnection", "LOSS.foreignConnection"),
            ("MIX", "metrics/classes/udp/foreignConnection", "MIX.udp.foreignConnection"),
            ("LAT", "metrics/udp.foreignConnection", "LAT.udp.foreignConnection"),
            ("LATLOAD", "metrics/udp.foreignConnection", "LATLOAD.udp.foreignConnection"),
            ("BASE", "metrics/loss/foreignConnection", "BASE.loss.foreignConnection"),
        })
        {
            var value = ArmAccess.Number(row, armName, path).Value;
            if (value is null)
            {
                continue;
            }

            readable++;
            if (value > 0.0)
            {
                rows.Add([
                    passId,
                    row.RunId,
                    armName,
                    label,
                    VerbatimNumber.Cell(value, 0),
                    "CORRECTNESS FAILURE: the product mixed datagrams between flows",
                ]);
            }
        }

        var (mix, _) = ArmAccess.ArmResult(row, "MIX");
        if (mix is not null && JsonValue.Dig(mix, "metrics/desktops") is { ValueKind: JsonValueKind.Array } desktops)
        {
            foreach (var entry in desktops.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var value = JsonValue.Number(entry, "udp.foreignConnection");
                if (value is not null and not 0.0)
                {
                    rows.Add([
                        passId,
                        row.RunId,
                        "MIX",
                        $"desktop {JsonText.Of(JsonValue.Member(entry, "desktop"))} udp.foreignConnection",
                        VerbatimNumber.Cell(value, 0),
                        "CORRECTNESS FAILURE: the product mixed datagrams between flows",
                    ]);
                }
            }
        }

        return readable;
    }

    private static void Identities(CampaignModel campaign, List<string> lines)
    {
        lines.Add("### 0.3 UDP accounting identities (asserted per record)");
        lines.Add(string.Empty);
        var rows = new List<IReadOnlyList<string>>();
        var held = 0;
        var uncheckedCount = 0;
        foreach (var passId in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(passId))
            {
                foreach (var check in IdentityChecks.Checks(row))
                {
                    string status;
                    switch (check.Ok)
                    {
                        case null:
                            uncheckedCount++;
                            status = "not checkable";
                            break;
                        case true:
                            held++;
                            continue;
                        case false when string.Equals(check.Kind, "scheduled-attempts", StringComparison.Ordinal):
                            status = "caveat — the values are kept and every cell that carries them says so";
                            break;
                        default:
                            status = "HARNESS ERROR — the record is excluded from the affected aggregates";
                            break;
                    }

                    if (check.Kind is "udp-identity" or "dns-partition" or "scheduled-attempts")
                    {
                        rows.Add([passId, row.RunId, check.Arm, check.Kind, status, check.Detail ?? NoGap]);
                    }
                }
            }
        }

        if (rows.Count > 0)
        {
            lines.Add(MarkdownTable.Render(["pass", "row", "arm", "assertion", "result", "detail"], rows));
            lines.Add(string.Empty);
        }

        lines.Add(
            $"{held.ToString(CultureInfo.InvariantCulture)} record-level identity check(s) **held** and "
            + $"{uncheckedCount.ToString(CultureInfo.InvariantCulture)} could not be checked (their "
            + "fields are not published by that record); only the ones that did not hold are listed above.");
        lines.Add(string.Empty);
        lines.Add(
            "`arrived + late + never + abandonedAtTeardown + corruptDatagrams == sent` is asserted per LOSS, MIX "
            + "and BASE record; the DNS records are checked for `answered + servfail + timeout + other == sent`, and "
            + "REL for `connectAttempts == scheduledAttempts`. A **violation** is reported as a harness error and "
            + "the affected UDP fields are excluded rather than averaged over. A `scheduledAttempts` mismatch means "
            + "the arm ended with work in flight: its values are kept but every cell that carries them says so.");
        lines.Add(string.Empty);
    }

    private static void AllBySeverity(Dictionary<string, List<Finding>> bySeverity, List<string> lines)
    {
        lines.Add("### 0.4 All findings by severity");
        lines.Add(string.Empty);
        foreach (var severity in Severity.Order)
        {
            var entries = bySeverity[severity];
            lines.Add($"**{severity} ({entries.Count.ToString(CultureInfo.InvariantCulture)})**");
            lines.Add(string.Empty);
            var detailRows = new List<IReadOnlyList<string>>(entries.Count);
            foreach (var finding in entries)
            {
                detailRows.Add([finding.Kind, finding.Scope, finding.Detail]);
            }

            lines.Add(detailRows.Count > 0
                ? MarkdownTable.Render(["kind", "scope", "detail"], detailRows)
                : "None.");
            lines.Add(string.Empty);
        }
    }
}
