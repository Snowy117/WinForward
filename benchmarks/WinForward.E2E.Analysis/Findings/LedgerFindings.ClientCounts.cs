using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Findings;

/// <summary>
/// The client side of the ledger's cross-checks: what each arm says it opened or sent, and how those
/// numbers are read against the target's own census.
/// </summary>
internal static partial class LedgerFindings
{
    /// <summary>Fills in each arm view's client-side counts and duration, which the views are read against.</summary>
    internal static void AttachClientCounts(CampaignModel campaign, IReadOnlyDictionary<string, LedgerPassView> passes)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(passes);

        foreach (var entry in passes.Values)
        {
            foreach (var arm in entry.PerArm)
            {
                var run = campaign.ById(arm.Pass, arm.Row);
                if (run is null)
                {
                    continue;
                }

                var (connections, connectionsWhy) = ClientConnectionCount(run, arm.Arm);
                var (datagrams, datagramsWhy) = ClientDatagramCount(run, arm.Arm);
                arm.ClientConnections = connections;
                arm.ClientConnectionsSource = connectionsWhy;
                arm.ClientDatagrams = datagrams;
                arm.ClientDatagramsSource = datagramsWhy;
                arm.DurationSeconds = RunClocks.ArmWindowSeconds(run, arm.Arm) ?? 0.0;
            }
        }
    }

    /// <summary>The endpoints one row's UDP echo arms saw, split by the path each arm was configured to take.</summary>
    internal static Dictionary<string, EndpointSlot> EndpointPartition(CampaignModel campaign, string passId, LedgerPassView entry)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(entry);

        var perRow = new Dictionary<string, EndpointSlot>(StringComparer.Ordinal);
        foreach (var arm in entry.PerArm)
        {
            if (!LedgerViewsBuilder.UdpEchoArms.Contains(arm.Arm, StringComparer.Ordinal))
            {
                continue;
            }

            var run = campaign.ById(passId, arm.Row);
            if (run is null)
            {
                continue;
            }

            var path = DeclaredUdpPath(run);
            if (path is null)
            {
                continue;
            }

            if (!perRow.TryGetValue(arm.Owner, out var slot))
            {
                slot = new EndpointSlot();
                perRow[arm.Owner] = slot;
            }

            var proxied = string.Equals(path, "proxied", StringComparison.Ordinal);
            var destination = proxied ? slot.Proxied : slot.Direct;
            foreach (var endpoint in arm.UdpEndpoints)
            {
                destination.Add(endpoint);
            }

            (proxied ? slot.ProxiedArms : slot.DirectArms).Add(ArmLabel(arm));
        }

        return perRow;
    }

    /// <summary>One arm window as the partition names it: the arm, or its lane with the arm.</summary>
    private static string ArmLabel(LedgerArmView arm)
    {
        if (string.Equals(arm.Row, arm.Owner, StringComparison.Ordinal))
        {
            return arm.Arm;
        }

        var slash = arm.Row.LastIndexOf('/');
        return $"{arm.Row[(slash + 1)..]}:{arm.Arm}";
    }

    /// <summary>The path a run's UDP echo traffic was configured to take, or null when its profile does not say.</summary>
    private static string? DeclaredUdpPath(ClientRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.RunId.Contains('/', StringComparison.Ordinal))
        {
            return run.RunId.EndsWith("/direct", StringComparison.Ordinal) ? "direct" : "proxied";
        }

        var profile = RowProfiles.Find(run.RunId);
        if (profile is null)
        {
            return null;
        }

        if (string.Equals(profile.Udp, RowProfiles.UdpNotCarried, StringComparison.Ordinal))
        {
            return "direct";
        }

        return profile.Udp is RowProfiles.UdpProxiedUtcp or RowProfiles.UdpProxiedNative ? "proxied" : null;
    }

    private static Measured<double?> ClientConnectionCount(ClientRun run, string armName)
    {
        var (result, why) = ArmAccess.ArmResult(run, armName);
        if (result is null)
        {
            return new(Value: null, Reason: why);
        }

        var kind = JsonValue.String(result, "kind");
        return kind switch
        {
            "latency" => new(Value: ArmAccess.Number(run, armName, "metrics/tcp.connectAttempts").Value,
                Reason: "metrics.tcp.connectAttempts (every connect the arm attempted: its lane connects + the 1 Hz connect probe)"),
            "reliability" => new(Value: ArmAccess.Number(run, armName, "metrics/connectAttempts").Value, Reason: "metrics.connectAttempts"),
            "throughput" => new(Value: ArmAccess.Number(run, armName, "parameters/streams").Value,
                Reason: "parameters.streams (one connection per stream)"),
            "persistent" => new(Value: ArmAccess.Number(run, armName, "metrics/connectAttempts").Value,
                Reason: "metrics.connectAttempts (a reconnect opens a second connection)"),
            "mix" => MixConnections(run, armName),
            "base" => new(Value: ArmAccess.Number(run, armName, "metrics/latency/tcp.connectAttempts").Value,
                Reason: "metrics.latency.tcp.connectAttempts (the latency phase's lane connects + its 1 Hz connect probe)"),
            "dns" => new(Value: null, Reason: "the DNS arm opens its TCP connection on the DNS port, which the ledger reports as dnsSummary"),
            _ => new(Value: null, Reason: $"{armName} opens no connection on the TCP echo port"),
        };
    }

    private static Measured<double?> MixConnections(ClientRun run, string armName)
    {
        var page = ArmAccess.Number(run, armName, "metrics/classes/page/connections").Value;
        var desktops = ArmAccess.Number(run, armName, "parameters/desktops").Value;
        if (page is null && desktops is null)
        {
            return new(Value: null, Reason: "no page-connection or desktop counter");
        }

        return new(Value: (page ?? 0.0) + (desktops ?? 0.0), Reason: "classes.page.connections + parameters.desktops (bulk streams)");
    }

    private static Measured<double?> ClientDatagramCount(ClientRun run, string armName)
    {
        var (result, why) = ArmAccess.ArmResult(run, armName);
        if (result is null)
        {
            return new(Value: null, Reason: why);
        }

        var kind = JsonValue.String(result, "kind");
        return kind switch
        {
            "latency" => new(Value: ArmAccess.Number(run, armName, "metrics/udp.sent").Value, Reason: "metrics.udp.sent"),
            "loss" => new(Value: ArmAccess.Number(run, armName, "metrics/sent").Value, Reason: "metrics.sent"),
            "mix" => new(Value: ArmAccess.Number(run, armName, "metrics/udp.sent").Value, Reason: "metrics.udp.sent"),
            "base" => BaseDatagrams(run, armName),
            "dns" => new(Value: null, Reason: "the DNS arm sends to the DNS port, which the ledger reports as dnsSummary"),
            _ => new(Value: null, Reason: $"{armName} sends no datagram to the UDP echo port"),
        };
    }

    private static Measured<double?> BaseDatagrams(ClientRun run, string armName)
    {
        var latencyLane = ArmAccess.Number(run, armName, "metrics/latency/udp.sent").Value;
        var lossPhase = ArmAccess.Number(run, armName, "metrics/loss/sent").Value;
        if (latencyLane is null && lossPhase is null)
        {
            return new(Value: null, Reason: "no UDP phase counter");
        }

        return new(Value: (latencyLane ?? 0.0) + (lossPhase ?? 0.0),
                Reason: "metrics.latency.udp.sent + metrics.loss.sent (both phases send datagrams)");
    }

    /// <summary>One row's UDP echo endpoints, bucketed by the path their arms were configured to take.</summary>
    internal sealed class EndpointSlot
    {
        /// <summary>Endpoints a proxied-path arm's window saw.</summary>
        internal HashSet<string> Proxied { get; } = new(StringComparer.Ordinal);

        /// <summary>Endpoints a direct-path arm's window saw.</summary>
        internal HashSet<string> Direct { get; } = new(StringComparer.Ordinal);

        /// <summary>The proxied-path arms that contributed, as the partition labels them.</summary>
        internal HashSet<string> ProxiedArms { get; } = new(StringComparer.Ordinal);

        /// <summary>The direct-path arms that contributed, as the partition labels them.</summary>
        internal HashSet<string> DirectArms { get; } = new(StringComparer.Ordinal);
    }

}
