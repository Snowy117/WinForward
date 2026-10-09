using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Analysis.Checks;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// §3.2, the measurement-validity gates: whether one (pass, row) record can be compared against another
/// at all — the client-loss checks, the lane witnesses, the accounting identities, the sampling and the
/// path-loss floor the pass's control block supplies.
/// </summary>
/// <remarks>
/// <para><b>Every check is stated with the numbers it compared.</b> The verdict's own note quotes them, so
/// a reader can see which term failed rather than only that something did.</para>
/// <para><b>A dirty control block fails every row in its pass.</b> The floor is the pass's, not the row's:
/// a path that lost traffic while nothing was loaded did not measure the product.</para>
/// </remarks>
internal static class GateValidity
{
    /// <summary>The loss rate below which the pass's control block counts as clean.</summary>
    private const double FloorThreshold = 1e-6;

    /// <summary>One (pass, row) measurement-validity row, in the reference's column order.</summary>
    internal static IReadOnlyList<string> Row(CampaignModel campaign, string passId, ClientRun row)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(row);

        var notes = new List<string>();
        var checks = new List<GateCheck>();
        LossChecks(row, checks);
        var (witnessCount, zeroWitnesses) = IdleAndWitnesses(row, checks, notes);
        var (identityFailures, identityUnchecked) = Identities(row, checks);
        var errors = Sampling(row, checks);
        var rejected = row.Arms.All.SelectMany(arm => arm.Samples).Count(sample => !RunSamples.IsReadable(sample));
        var (preLoss, postLoss) = Floor(campaign, row, checks, notes);
        var verdict = GateFlow.Verdict(checks, notes);

        return
        [
            passId,
            row.RunId,
            ClientLoss(row, "LOSS"),
            ClientLoss(row, "LAT"),
            ClientLoss(row, "LATLOAD"),
            row.Arms.Contains("LAT")
                ? VerbatimNumber.Cell(ArmAccess.Number(row, "LAT", "gates/inFlightCeilingMs").Value, 1)
                : "n/a",
            row.Arms.Contains("MIX")
                ? VerbatimNumber.Cell(ArmAccess.Number(row, "MIX", "gates/idleLanes").Value, 0)
                : "n/a",
            witnessCount > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{zeroWitnesses.Count} zero of {witnessCount}")
                : "n/a",
            IdentityCell(identityFailures, identityUnchecked),
            string.Create(CultureInfo.InvariantCulture, $"{errors.Count} / {rejected}"),
            preLoss is not null ? VerbatimNumber.Cell(preLoss) : "n/a",
            postLoss is not null ? VerbatimNumber.Cell(postLoss) : "n/a",
            verdict,
            notes.Count > 0 ? string.Join("; ", notes) : "—",
        ];
    }

    private static string IdentityCell(List<string> failures, List<string> uncheckedFindings)
    {
        if (failures.Count > 0)
        {
            return "FAIL: " + string.Join("; ", failures);
        }

        return uncheckedFindings.Count > 0
            ? $"holds ({uncheckedFindings.Count} not checkable)"
            : "n/a (no identity published)";
    }

    private static void LossChecks(ClientRun row, List<GateCheck> checks)
    {
        foreach (var armName in new[] { "LOSS", "LAT", "LATLOAD", "BASE" })
        {
            if (!row.Arms.Contains(armName))
            {
                continue;
            }

            var (value, why) = ArmAccess.Number(row, armName, "gates/clientSendLoss");
            if (value is null)
            {
                (value, why) = ArmAccess.Number(row, armName, "metrics/clientSendLoss");
            }

            if (value is null)
            {
                checks.Add(new GateCheck($"{armName} clientSendLoss", CheckState.Unknown, why ?? "missing"));
            }
            else
            {
                checks.Add(new GateCheck(
                    $"{armName} clientSendLoss",
                    value == 0.0 ? CheckState.Pass : CheckState.Fail,
                    $"{VerbatimNumber.Cell(value, 0)} == 0"));
            }

            ZeroCheck(checks, row, armName, "gates/backlogDrops", "backlogDrops");
            ZeroCheck(checks, row, armName, "gates/scheduleTruncated", "scheduleTruncated");
            ZeroCheck(checks, row, armName, "gates/laneShortfall", "laneShortfall");
            CeilingCheck(checks, row, armName);
        }
    }

    private static void ZeroCheck(List<GateCheck> checks, ClientRun row, string armName, string path, string name)
    {
        var value = ArmAccess.Number(row, armName, path).Value;
        if (value is not null)
        {
            checks.Add(new GateCheck(
                $"{armName} {name}",
                value == 0.0 ? CheckState.Pass : CheckState.Fail,
                $"{VerbatimNumber.Cell(value, 0)} == 0"));
        }
    }

    private static void CeilingCheck(List<GateCheck> checks, ClientRun row, string armName)
    {
        var overflow = ArmAccess.Number(row, armName, "gates/windowOverflow").Value;
        if (overflow is null)
        {
            return;
        }

        var ceiling = VerbatimNumber.Cell(ArmAccess.Number(row, armName, "gates/inFlightCeilingMs").Value, 1);
        checks.Add(new GateCheck(
            $"{armName} latencyCeiling",
            CheckState.Warn,
            overflow > 0.0
                ? $"{VerbatimNumber.Cell(overflow, 0)} deferral(s); direct-latency ceiling {ceiling} ms reached, the tail is measured through the deferred queue"
                : $"not reached (inFlightCeilingMs={ceiling})"));
    }

    private static (int Count, List<string> Zero) IdleAndWitnesses(ClientRun row, List<GateCheck> checks, List<string> notes)
    {
        var idle = ArmAccess.Number(row, "MIX", "gates/idleLanes").Value;
        if (idle is not null)
        {
            checks.Add(new GateCheck(
                "MIX idleLanes",
                idle == 0.0 ? CheckState.Pass : CheckState.Fail,
                $"{VerbatimNumber.Cell(idle, 0)} == 0"));
        }

        var count = 0;
        var zero = new List<string>();
        foreach (var armName in ArmRecords.LoadOrder)
        {
            var witnesses = IdentityChecks.LaneWitnesses(row, armName);
            if (witnesses is null)
            {
                continue;
            }

            foreach (var (label, ok) in witnesses)
            {
                count++;
                if (!ok)
                {
                    zero.Add($"{armName} {label}");
                }
            }
        }

        if (count > 0)
        {
            checks.Add(new GateCheck(
                "lane witnesses",
                zero.Count == 0 ? CheckState.Pass : CheckState.Fail,
                string.Create(CultureInfo.InvariantCulture, $"{zero.Count} of {count} witnesses are zero")));
            if (zero.Count > 0)
            {
                notes.Add("zero witnesses: " + string.Join("; ", zero));
            }
        }

        return (count, zero);
    }

    private static (List<string> Failures, List<string> Unchecked) Identities(ClientRun row, List<GateCheck> checks)
    {
        var failures = new List<string>();
        var uncheckedFindings = new List<string>();
        var warnings = new List<string>();
        foreach (var check in IdentityChecks.Checks(row))
        {
            if (check.Kind is not ("udp-identity" or "dns-partition" or "scheduled-attempts"))
            {
                continue;
            }

            switch (check.Ok)
            {
                case false when string.Equals(check.Kind, "scheduled-attempts", StringComparison.Ordinal):
                    warnings.Add($"{check.Arm} {check.Kind} ({check.Detail})");
                    break;
                case false:
                    failures.Add($"{check.Arm} {check.Kind} ({check.Detail})");
                    break;
                case null:
                    uncheckedFindings.Add($"{check.Arm} {check.Kind} ({check.Detail})");
                    break;
            }
        }

        if (warnings.Count > 0)
        {
            checks.Add(new GateCheck("scheduledAttempts", CheckState.Warn, string.Join("; ", warnings)));
        }

        if (failures.Count > 0 || uncheckedFindings.Count > 0)
        {
            checks.Add(new GateCheck(
                "accounting identity",
                failures.Count > 0 ? CheckState.Fail : CheckState.Pass,
                string.Join("; ", failures.Count > 0 ? failures : uncheckedFindings)));
        }

        return (failures, uncheckedFindings);
    }

    private static List<JsonElement> Sampling(ClientRun row, List<GateCheck> checks)
    {
        var errors = RunSamples.SamplerErrors(row);
        var rejected = row.Arms.All.SelectMany(arm => arm.Samples).Count(sample => !RunSamples.IsReadable(sample));
        checks.Add(new GateCheck(
            "sampling",
            errors.Count == 0 && rejected == 0 ? CheckState.Pass : CheckState.Fail,
            string.Create(CultureInfo.InvariantCulture, $"{errors.Count} samplerError, {rejected} rejected sample(s)")));
        return errors;
    }

    private static (double? Pre, double? Post) Floor(
        CampaignModel campaign,
        ClientRun row,
        List<GateCheck> checks,
        List<string> notes)
    {
        var (pre, preWhy) = BaseLossRate(campaign, row, RowProfiles.ControlPre);
        var (post, postWhy) = BaseLossRate(campaign, row, RowProfiles.ControlPost);
        foreach (var (source, value, why) in new[]
        {
            (RowProfiles.ControlPre, pre, preWhy),
            (RowProfiles.ControlPost, post, postWhy),
        })
        {
            if (value is null)
            {
                checks.Add(new GateCheck($"BASE floor ({source})", CheckState.Unknown, why ?? "missing"));
            }
            else
            {
                checks.Add(new GateCheck(
                    $"BASE floor ({source})",
                    value < FloorThreshold ? CheckState.Pass : CheckState.Fail,
                    $"{VerbatimNumber.Exponential(value.Value, 3)} < 1e-06"));
            }
        }

        if (pre >= FloorThreshold || post >= FloorThreshold)
        {
            notes.Add("the pass's control block is dirty, so every row in this pass inherits a failed harness floor");
        }

        return (pre, post);
    }

    private static Measured<double?> BaseLossRate(CampaignModel campaign, ClientRun row, string controlId)
    {
        var control = campaign.InPass(row.PassId, controlId);
        return control is null
            ? new(Value: null, Reason: $"no {controlId} row in {row.PassId}")
            : ArmAccess.Number(control, "BASE", "metrics/loss/lossRate");
    }

    private static string ClientLoss(ClientRun row, string armName) =>
        row.Arms.Contains(armName)
            ? VerbatimNumber.Fixed(ArmAccess.Number(row, armName, "gates/clientSendLoss").Value ?? 0.0, 0)
            : "n/a";
}
