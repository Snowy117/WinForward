using System.Globalization;
using WinForward.E2E.Analysis.Checks;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Release;

/// <summary>
/// Reads one loaded campaign's gate facts into <see cref="ReleaseGateEvidence"/>, without deciding any
/// metric cell itself.
/// </summary>
/// <remarks>
/// The reader is deliberately thin: it asks the same arm readers the legacy tables use for each published
/// gate value, asks the identity check for the UDP accounting verdict, and asks the control-drift
/// computation for the block comparison. The matrix alone maps those facts to metric cells.
/// </remarks>
internal static class ReleaseGateEvidenceReader
{
    /// <summary>The BASE path-loss rate below which a pass's control block counts as clean.</summary>
    private const double FloorThreshold = 1e-6;

    /// <summary>How many producing sites one campaign-level detail sentence quotes.</summary>
    private const int DetailLimit = 4;

    /// <summary>Reads every pass and row of the campaign into one evidence set.</summary>
    /// <param name="campaign">The loaded campaign.</param>
    /// <returns>The gate facts, with campaign-level details already rendered.</returns>
    internal static ReleaseGateEvidence Read(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var arms = new List<ReleaseGateArm>();
        var sampling = new List<ReleaseGateSampling>();
        var foreign = new List<string>();
        var leaks = new List<string>();
        foreach (var passId in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(passId))
            {
                ReadRow(passId, row, arms, sampling, foreign, leaks);
            }
        }

        var drift = ControlDrift.Compute(campaign).Comparisons
            .FirstOrDefault(entry => string.Equals(entry.Verdict, "different", StringComparison.Ordinal));
        var baseFloor = BaseFloor(campaign);
        return new ReleaseGateEvidence
        {
            Arms = arms,
            Sampling = sampling,
            ForeignConnection = foreign.Count > 0,
            ForeignConnectionDetail = Quote(
                "the product delivered datagrams belonging to one flow into another",
                foreign),
            DirectLeak = leaks.Count > 0,
            DirectLeakDetail = Quote(
                "the product intercepted application traffic its configuration sends direct",
                leaks),
            ControlDrift = drift is not null,
            ControlDriftDetail = drift?.Statement,
            BaseFloorFailed = baseFloor.Count > 0,
            BaseFloorDetail = Quote(
                "a control block lost traffic, so the pass's path quality is qualified; this is a campaign caveat, not a cell invalidation",
                baseFloor),
        };
    }

    private static void ReadRow(
        string passId,
        ClientRun row,
        List<ReleaseGateArm> arms,
        List<ReleaseGateSampling> sampling,
        List<string> foreign,
        List<string> leaks)
    {
        var (udpIdentities, dnsIdentities) = IdentitiesOf(row);
        arms.AddRange(row.Arms.All.Select(arm => new ReleaseGateArm
        {
            Pass = passId,
            RowId = row.RunId,
            Arm = arm.Name,
            WindowOverflow = Number(row, arm.Name, "gates/windowOverflow"),
            ClientSendLoss = Number(row, arm.Name, "gates/clientSendLoss"),
            BacklogDrops = Number(row, arm.Name, "gates/backlogDrops"),
            ScheduleTruncated = Number(row, arm.Name, "gates/scheduleTruncated"),
            LaneShortfall = Number(row, arm.Name, "gates/laneShortfall"),
            UdpIdentityViolated = udpIdentities.TryGetValue(arm.Name, out var udpDetail),
            UdpIdentityDetail = udpDetail,
            DnsPartitionViolated = dnsIdentities.TryGetValue(arm.Name, out var dnsDetail),
            DnsPartitionDetail = dnsDetail,
        }));

        var errors = RunSamples.SamplerErrors(row).Count;
        var rejected = row.Arms.All
            .SelectMany(arm => arm.Samples)
            .Count(sample => !RunSamples.IsReadable(sample));
        if (errors > 0 || rejected > 0)
        {
            sampling.Add(new ReleaseGateSampling(passId, row.RunId, errors, rejected));
        }

        foreign.AddRange(IdentityChecks.ForeignConnectionSources(row)
            .Select(source => $"{passId}/{row.RunId} {source.Arm} {source.Label}={VerbatimNumber.Cell(source.Count, 0)}"));
        if (JsonValue.Number(row.DualTruth, "directLeak") is > 0.0 and var leak)
        {
            leaks.Add($"{passId}/{row.RunId} directLeak={VerbatimNumber.Cell(leak, 0)}");
        }
    }

    private static double? Number(ClientRun row, string arm, string path) => ArmAccess.Number(row, arm, path).Value;

    private static (Dictionary<string, string?> Udp, Dictionary<string, string?> Dns) IdentitiesOf(ClientRun row)
    {
        var failed = IdentityChecks.Checks(row).Where(check => check.Ok is false).ToList();
        return (
            failed
                .Where(check => string.Equals(check.Kind, "udp-identity", StringComparison.Ordinal))
                .ToDictionary(check => check.Arm, check => check.Detail, StringComparer.Ordinal),
            failed
                .Where(check => string.Equals(check.Kind, "dns-partition", StringComparison.Ordinal))
                .ToDictionary(check => check.Arm, check => check.Detail, StringComparer.Ordinal));
    }

    private static List<string> BaseFloor(CampaignModel campaign) =>
        [.. campaign.PassIds
            .SelectMany(passId => new[] { RowProfiles.ControlPre, RowProfiles.ControlPost }
                .Select(controlId => (PassId: passId, ControlId: controlId, Control: campaign.InPass(passId, controlId))))
            .Where(entry => entry.Control is not null)
            .Select(entry => (entry.PassId, entry.ControlId, Loss: ArmAccess.Number(entry.Control!, "BASE", "metrics/loss/lossRate").Value))
            .Where(entry => entry.Loss is >= FloorThreshold)
            .Select(entry => $"{entry.PassId}/{entry.ControlId} BASE lossRate={VerbatimNumber.Cell(entry.Loss)}")];

    private static string? Quote(string summary, List<string> sites)
    {
        if (sites.Count == 0)
        {
            return null;
        }

        var head = string.Join("; ", sites.Take(DetailLimit));
        var more = sites.Count > DetailLimit
            ? string.Create(CultureInfo.InvariantCulture, $" and {sites.Count - DetailLimit} more site(s)")
            : string.Empty;
        return $"{summary}: {head}{more}";
    }
}
