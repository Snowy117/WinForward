using System.Globalization;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// §3, "Gate table": the two flow gates per (pass, row), the measurement-validity gates that say whether
/// a record can be compared at all, and the per-row summary of both.
/// </summary>
/// <remarks>
/// <para><b>Per-pass values, not aggregates.</b> Every line is one measurement, because a gate that failed
/// in one pass is not a gate that failed on average.</para>
/// <para><b>A <c>FAIL</c> is a statement, not a score.</b> It says the record cannot be compared against
/// another without saying so; a <c>warn</c> is disclosed and does not fail the row.</para>
/// </remarks>
internal static class TableGates
{
    /// <summary>The section's body, without its heading and ending in a newline.</summary>
    /// <remarks>
    /// The caption after §3.1 is followed by <c>### 3.2</c> with **no** blank line between them: the
    /// reference appends the two back to back, so a blank line inserted there is a line the
    /// document does not have.
    /// </remarks>
    internal static string RenderBody(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var lines = new List<string>
        {
            "Per-pass values, not aggregates: every line is one (pass, row) measurement. `tcpGate` and `udpGate` "
            + "are the two flow gates; the measurement-validity table below carries the client-loss, lane-witness, "
            + "identity and sampling checks. A cell that cannot be computed prints `n/a (<reason>)`.",
            string.Empty,
            "### 3.1 Flow gates and UDP carriage",
            string.Empty,
            MarkdownTable.Render(
                [
                    "pass", "row", "presence", "tcpAttempts", "truth tcp", "tcpGate", "udpArms", "truth udp (native)",
                    "truth utcp", "udpGate", "carriage", "expected", "UDP/53 carriage", "flow verdict", "verdict", "notes",
                ],
                GateFlow.Rows(campaign)),
            string.Empty,
            Caption(),
            "### 3.2 Measurement validity gates",
            string.Empty,
            "Every value is one (pass, row) record. A `FAIL` here means the record cannot be compared against "
            + "another without saying so; `warn` rows are disclosed and do not fail the row. The latency arms `LAT` "
            + "and `LATLOAD` are gated on the same client-loss check the `LOSS` arm always had, and additionally on "
            + "their own `backlogDrops` (samples the arm destroyed), `scheduleTruncated` (part of the offered "
            + "schedule never offered), `laneShortfall` and the lane witnesses.",
            string.Empty,
            MarkdownTable.Render(
                [
                    "pass", "row", "LOSS clientSendLoss", "LAT clientSendLoss", "LATLOAD clientSendLoss",
                    "LAT inFlightCeilingMs", "MIX idleLanes", "lane witnesses", "accounting identity",
                    "samplerError / rejected", "BASE lossRate pre", "BASE lossRate post", "verdict", "notes",
                ],
                ValidityRows(campaign)),
            string.Empty,
            "### 3.3 Gate verdict summary per row",
            string.Empty,
            MarkdownTable.Render(["row", "passes", "flow gates", "measurement validity"], SummaryRows(campaign)),
            string.Empty,
        };

        return string.Join('\n', lines);
    }

    private static List<IReadOnlyList<string>> ValidityRows(CampaignModel campaign)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var passId in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(passId))
            {
                rows.Add(GateValidity.Row(campaign, passId, row));
            }
        }

        return rows;
    }

    private static List<IReadOnlyList<string>> SummaryRows(CampaignModel campaign)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var rowId in campaign.RowIds)
        {
            var flowVerdicts = new List<string>();
            var validityVerdicts = new List<string>();
            foreach (var passId in campaign.PassIds)
            {
                var row = campaign.InPass(passId, rowId);
                if (row is null)
                {
                    continue;
                }

                flowVerdicts.Add(GateFlow.For(passId, row)[^2]);
                validityVerdicts.Add(GateValidity.Row(campaign, passId, row)[^2]);
            }

            rows.Add(
            [
                rowId,
                flowVerdicts.Count.ToString(CultureInfo.InvariantCulture),
                DescriptiveStats.SummariseVerdicts(flowVerdicts),
                DescriptiveStats.SummariseVerdicts(validityVerdicts),
            ]);
        }

        return rows;
    }

    private static string Caption()
    {
        var incapable = string.Join(", ", RowProfiles.UdpIncapableRows.Order(StringComparer.Ordinal));
        var gateMin = VerbatimNumber.Fixed(GateFlow.TcpGateMin, 2);
        return $"""
            **Two gates, both per (pass, row).** `proxy-truth.json` counts *flows*, not datagrams: the orchestrator logs one inbound TCP connection per TCP connection, one inbound packet connection per native `UDP ASSOCIATE` flow (`udp`) and one UoT control connection per UDP-over-TCP v2 flow (`utcp`). The LOSS arm pushes thousands of datagrams through a single UDP socket, so it contributes exactly one UDP flow.

            ```
            tcpAttempts = LAT.tcp.connectAttempts + LATLOAD.tcp.connectAttempts
                        + REL.connectAttempts + PERSIST.connectAttempts
                        + THRU.streams (parameters.streams, else 1)
                        + DNS.tcpConnections (1 when metrics.tcpSent > 0, else 0)
                        + MIX.pageConnections + MIX.parameters.desktops
            tcpGate     = proxy-truth.tcp / tcpAttempts                  >= {gateMin} required

            udpArms     = UDP sockets the row's plan is expected to relay:
                          LAT + LATLOAD + LOSS + MIX, plus DNSALT, plus DNS
                          only when the row's profile relays port 53
            udpGate     = (proxy-truth.udp + proxy-truth.utcp) > 0 required
            ```

            **The TCP gate is the exact one.** A ratio slightly under 1 is legitimate: a connection attempt that fails before the proxy ever sees a SYN (REL's `connectFail` outcomes) never produces a proxied flow, which is why the bar is {gateMin} rather than 1. Both the denominator and the numerator are printed, never just the ratio. `PERSIST` is in the denominator because it opens TCP connections like any other arm, and `DNS` contributes **one** connection however many queries it pipelines over it — an earlier version counted `DNS.tcpSent` queries as flows, which inflated the denominator and could hide a leak; this file counts connections, as the gate requires.

            **The UDP gate is a presence check, and the arrival accounting is the real evidence.** It only asks that at least one UDP flow reached the proxy, because the flow count is not comparable across products: a product that multiplexes every UDP flow over one association reports one flow for the whole run while its datagrams still arrive. What proves a working UDP path is the `LOSS` arm's own `arrived / sent`, reported in section 8. The DNS arms' own UDP sockets are counted only when the row's profile says that port is relayed: a local-target or hardcoded port-53 datagram never reaches the proxy, so counting it would demand a flow that cannot exist. `{incapable}` is exempt from the UDP gate entirely — it cannot proxy UDP — and every UDP cell of that row reads `{RowProfiles.NotCarriedCell}` rather than a number.

            The `carriage` column is what the ledger of flows shows (`utcp` when `utcp > 0`, `native` when `udp > 0`, else `none`) against what the row's own profile declares. A mismatch is a gate failure, which is what catches a configuration wired to the wrong UDP carriage. The `UDP/53 carriage` column states what the row does with destination port 53 — relayed, direct through the local DNS target, or direct through a hardcoded pass-through — because those rows' port-53 DNS arm is a direct-path measurement and must never be read as a proxied result.

            The `{RowProfiles.ControlPre}` and `{RowProfiles.ControlPost}` rows are exempt from the flow gates: nothing is loaded, both numerators are legitimately near zero, and their role is to bracket the product block within each pass — which is also what makes the post block the only instrument in the campaign that can detect a product that left a driver filtering after it exited.
            """;
    }
}
