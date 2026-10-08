using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// The client-side denominators behind the two flow gates, and the terms that make them up.
/// </summary>
/// <remarks>
/// <b>Flows, not datagrams or queries.</b> <c>proxy-truth.json</c> counts one inbound connection per TCP
/// connection and one per UDP association, so the DNS arm contributes exactly one TCP connection however
/// many queries it pipelines, and the LOSS arm exactly one UDP flow however many datagrams it sends.
/// </remarks>
internal sealed class FlowModel
{
    /// <summary>The client's own TCP connection attempts, summed over the row's arms.</summary>
    internal double TcpAttempts { get; set; }

    /// <summary>The terms the TCP denominator is made of, in the order they were added.</summary>
    internal List<(string Label, double Value)> TcpTerms { get; } = [];

    /// <summary>How many of the row's arms carry UDP.</summary>
    internal double UdpArms { get; set; }

    /// <summary>The arms that carry UDP.</summary>
    internal List<string> UdpTerms { get; } = [];

    /// <summary>What the row's arms did not publish, which is what the denominator is missing.</summary>
    internal List<string> Missing { get; } = [];

    /// <summary>The TCP denominator spelled out, or <c>n/a</c> when it has no terms.</summary>
    internal string TcpFormula() => TcpTerms.Count == 0
        ? "n/a"
        : string.Join(
            " + ",
            TcpTerms.Select(term => $"{term.Label}={VerbatimNumber.Cell(term.Value, 0)}"));

    /// <summary>The UDP arms that carry traffic, or <c>n/a</c>.</summary>
    internal string UdpFormula() => UdpTerms.Count > 0 ? string.Join(", ", UdpTerms) : "n/a";
}

/// <summary>
/// Builds the client-side denominator model of one row: the arms that opened TCP connections, the arms
/// that carried UDP, and what the row did not publish.
/// </summary>
internal static class FlowModels
{
    /// <summary>The client-side denominator model for one row.</summary>
    internal static FlowModel For(ClientRun row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var model = new FlowModel();
        LatencyArms(row, model);
        ReliabilityAndThroughput(row, model);
        MixArm(row, model);
        UdpArms(row, model);

        if (model.TcpAttempts <= 0.0)
        {
            model.Missing.Add("no client-side TCP connection counters");
        }

        if (model.UdpArms <= 0.0
            && !CampaignQueries.IsControl(row.RunId)
            && !RowProfiles.UdpIncapableRows.Contains(row.RunId))
        {
            model.Missing.Add("no UDP-carrying arm");
        }

        return model;
    }

    private static void LatencyArms(ClientRun row, FlowModel model)
    {
        foreach (var armName in new[] { "LAT", "LATLOAD" })
        {
            var present = row.Arms.Contains(armName);
            AddTcp(model, $"{armName}.tcp.connectAttempts", ArmAccess.Number(row, armName, "metrics/tcp.connectAttempts").Value, present);
            var udpSent = ArmAccess.Number(row, armName, "metrics/udp.sent").Value;
            AddUdpArm(model, armName, present ? (udpSent ?? 0.0) > 0.0 : null, present);
        }
    }

    private static void ReliabilityAndThroughput(ClientRun row, FlowModel model)
    {
        var relPresent = row.Arms.Contains("REL");
        AddTcp(model, "REL.connectAttempts", ArmAccess.Number(row, "REL", "metrics/connectAttempts").Value, relPresent);

        var persistPresent = row.Arms.Contains("PERSIST");
        AddTcp(model, "PERSIST.connectAttempts", ArmAccess.Number(row, "PERSIST", "metrics/connectAttempts").Value, persistPresent);

        var thruPresent = row.Arms.Contains("THRU");
        var (thruResult, _) = ArmAccess.ArmResult(row, "THRU");
        var streams = thruResult is not null ? JsonValue.Number(JsonValue.Dig(thruResult, "parameters"), "streams") : null;
        if (thruPresent && streams is null)
        {
            streams = 1.0;
        }

        AddTcp(model, "THRU.streams", streams, thruPresent);

        var dnsPresent = row.Arms.Contains("DNS");
        var dnsTcp = ArmAccess.Number(row, "DNS", "metrics/tcpSent").Value;
        double? dnsConnections = null;
        if (dnsTcp is not null)
        {
            dnsConnections = dnsTcp > 0.0 ? 1.0 : 0.0;
        }

        AddTcp(model, "DNS.tcpConnections", dnsConnections, dnsPresent);
    }

    private static void MixArm(ClientRun row, FlowModel model)
    {
        var mixPresent = row.Arms.Contains("MIX");
        var mixConnections = ArmAccess.Number(row, "MIX", "metrics/classes/page/connections").Value;
        mixConnections ??= ArmAccess.Number(row, "MIX", "metrics/pageConnections").Value;
        AddTcp(model, "MIX.pageConnections", mixConnections, mixPresent);
        var (mixResult, _) = ArmAccess.ArmResult(row, "MIX");
        var desktops = mixResult is not null ? JsonValue.Number(JsonValue.Dig(mixResult, "parameters"), "desktops") : null;
        AddTcp(model, "MIX.desktops(bulk)", desktops, mixPresent);

        bool? mixCarries = null;
        if (mixPresent)
        {
            var mixUdpRate = mixResult is not null
                ? JsonValue.Number(JsonValue.Dig(mixResult, "parameters"), "udpPacketsPerSecondPerDesktop")
                : null;
            if (mixUdpRate is not null)
            {
                mixCarries = mixUdpRate > 0.0;
            }
            else
            {
                var mixUdpSent = ArmAccess.Number(row, "MIX", "metrics/classes/udp/sent").Value;
                mixCarries = mixUdpSent > 0.0;
            }
        }

        AddUdpArm(model, "MIX", mixCarries, mixPresent);
    }

    private static void UdpArms(ClientRun row, FlowModel model)
    {
        var lossPresent = row.Arms.Contains("LOSS");
        var lossSent = ArmAccess.Number(row, "LOSS", "metrics/sent").Value;
        AddUdpArm(model, "LOSS", Carries(lossSent), lossPresent);

        var profile = RowProfiles.Find(row.RunId);
        var dnsaltPresent = row.Arms.Contains("DNSALT");
        var dnsaltUdp = ArmAccess.Number(row, "DNSALT", "metrics/udp.sent").Value;
        AddUdpArm(model, "DNSALT", Carries(dnsaltUdp), dnsaltPresent);

        var dnsPresent = row.Arms.Contains("DNS");
        var dnsUdp = ArmAccess.Number(row, "DNS", "metrics/udp.sent").Value;
        var dnsRelayed = profile is not null && string.Equals(profile.Udp53, RowProfiles.Udp53Relayed, StringComparison.Ordinal);
        AddUdpArm(model, "DNS", dnsUdp is null ? null : dnsUdp > 0.0 && dnsRelayed, dnsPresent);
    }

    /// <summary>Whether a counter says the arm carried traffic, or null when the arm published none.</summary>
    private static bool? Carries(double? value) => value.HasValue ? value.Value > 0.0 : null;

    private static void AddTcp(FlowModel model, string label, double? value, bool armPresent)
    {
        if (!armPresent)
        {
            model.Missing.Add($"no {label.Split('.')[0]} arm");
        }
        else if (value is null)
        {
            model.Missing.Add(label);
        }
        else
        {
            model.TcpTerms.Add((label, value.Value));
            model.TcpAttempts += value.Value;
        }
    }

    private static void AddUdpArm(FlowModel model, string label, bool? carries, bool armPresent)
    {
        if (!armPresent)
        {
            model.Missing.Add($"no {label} arm");
        }
        else if (carries is null)
        {
            model.Missing.Add($"{label} UDP indication");
        }
        else if (carries.Value)
        {
            model.UdpTerms.Add(label);
            model.UdpArms++;
        }
    }
}
