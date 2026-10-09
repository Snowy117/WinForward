using WinForward.E2E.Analysis.Metrics;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// The long-lived connection detail: one line per row, the <c>PERSIST</c> arm's throughput, its idle
/// gap, the round trips either side of it, and the one-sentence verdict on whether the connection held.
/// </summary>
/// <remarks>
/// <para><b>A broken idle connection is a headline result.</b> An application that expects one long
/// connection behaves differently the moment the product replaces it, so the verdict column says
/// <c>BROKE THE IDLE CONNECTION</c> in capitals rather than burying the fact in a counter.</para>
/// <para><b>Two independent pieces of evidence, one verdict.</b> <c>survivedIdle</c> is what the client
/// observed and <c>reconnects</c> is what it had to do about it; either one alone is enough to call the
/// connection broken, and the verdict names both.</para>
/// <para><b>The flag is listed per pass, not aggregated.</b> "Survived two of three passes" is not a
/// measurement, and the pass that broke is the one a reader needs to see.</para>
/// </remarks>
internal static class TablePersist
{
    private const string Arm = "PERSIST";

    private const string HeldVerdict = "held the connection across the idle gap";

    private const string BrokeVerdict = "BROKE THE IDLE CONNECTION";

    private const string ReconnectsVerdict = "; reconnects>0";

    private const string NoRecord = "n/a (no PERSIST record)";

    private const string Unavailable = "n/a";

    private static readonly string[] s_headers =
    [
        "row",
        "requests",
        "responses",
        "responseRate",
        "reconnects",
        "survivedIdle per pass",
        "idle s scheduled",
        "idle s observed",
        "tcp-rtt p50",
        "tcp-rtt p99",
        "verdict",
    ];

    /// <summary>The section's body, without its heading and ending in a newline.</summary>
    /// <param name="campaign">The loaded campaign every arm is read from.</param>
    /// <returns>The body's text.</returns>
    internal static string RenderBody(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var rows = new List<IReadOnlyList<string>>(campaign.RowIds.Count);
        foreach (var rowId in campaign.RowIds)
        {
            rows.Add(Line(campaign, rowId));
        }

        var lines = new List<string>
        {
            "`PERSIST` holds one TCP connection open across a paced workload with an idle gap in the middle. "
            + "`survivedIdle=false` or `reconnects>0` means the product dropped or broke a long-lived connection, which is a "
            + "headline result rather than a footnote: a product that cannot keep a connection alive changes the behaviour "
            + "of every application that expects one. Cells are `median [p25–p75] across passes (n=K)`; `survivedIdle` is a "
            + "per-pass fact, so it is listed per pass.",
            string.Empty,
            MarkdownTable.Render(s_headers, rows),
            string.Empty,
        };

        return string.Join('\n', lines);
    }

    /// <summary>One row's line, or the reason its plan never ran the arm.</summary>
    private static List<string> Line(CampaignModel campaign, string rowId)
    {
        var (status, reason) = MetricStatus.Resolve(campaign, Arm, udpPath: null, dns53: false, rowId);
        if (status is MetricStatus.NotInPlan or MetricStatus.DeclaredAbsent)
        {
            return [rowId, .. Enumerable.Repeat($"n/a ({reason})", s_headers.Length - 1)];
        }

        var survived = Survived(campaign, rowId, out var broke, out var reconnected);
        return
        [
            rowId,
            Stat(campaign, rowId, "metrics/requests"),
            Stat(campaign, rowId, "metrics/responses"),
            Stat(campaign, rowId, "metrics/responseRate", scale: 100.0, unit: " %"),
            Stat(campaign, rowId, "metrics/reconnects"),
            survived.Count > 0 ? string.Join("; ", survived) : Unavailable,
            Stat(campaign, rowId, "metrics/idleSecondsScheduled", unit: " s"),
            Stat(campaign, rowId, "metrics/idleSecondsObserved", unit: " s"),
            Latency(campaign, rowId, "p50Us"),
            Latency(campaign, rowId, "p99Us"),
            Verdict(survived, broke, reconnected),
        ];
    }

    /// <summary>What each pass said about the idle connection, and whether any pass broke it.</summary>
    private static List<string> Survived(CampaignModel campaign, string rowId, out List<string> broke, out bool reconnected)
    {
        var survived = new List<string>();
        broke = [];
        reconnected = false;
        foreach (var passId in campaign.PassIds)
        {
            var row = campaign.InPass(passId, rowId);
            if (row is null)
            {
                continue;
            }

            if (ArmAccess.Number(row, Arm, "metrics/reconnects").Value is { } count && count.CompareTo(0.0) != 0)
            {
                reconnected = true;
            }

            var (value, why) = ArmAccess.Flag(row, Arm, "metrics/survivedIdle");
            if (value is null)
            {
                survived.Add($"{passId}: n/a ({why})");
                continue;
            }

            survived.Add($"{passId}: {(value.Value ? "yes" : "NO")}");
            if (!value.Value)
            {
                broke.Add(passId);
            }
        }

        return survived;
    }

    /// <summary>The one sentence the row's connection gets: broken, held, or never measured.</summary>
    private static string Verdict(List<string> survived, List<string> broke, bool reconnected)
    {
        if (broke.Count > 0 || reconnected)
        {
            var verdict = BrokeVerdict;
            if (broke.Count > 0)
            {
                verdict += $" in {string.Join(", ", broke)}";
            }

            return reconnected ? verdict + ReconnectsVerdict : verdict;
        }

        return survived.Count > 0 ? HeldVerdict : NoRecord;
    }

    /// <summary>One counter aggregated across passes, with the null passes counted beside it.</summary>
    private static string Stat(
        CampaignModel campaign,
        string rowId,
        string path,
        double scale = 1.0,
        string unit = "")
    {
        var values = new List<double>();
        var nulls = 0;
        foreach (var passId in campaign.PassIds)
        {
            if (campaign.InPass(passId, rowId) is not { } row)
            {
                continue;
            }

            var (value, why) = ArmAccess.Number(row, Arm, path);
            if (value is null)
            {
                if (string.Equals(why, ArmAccess.NullRateReason, StringComparison.Ordinal))
                {
                    nulls++;
                }

                continue;
            }

            values.Add(value.Value * scale);
        }

        return DescriptiveStats.FmtStat(values, digits: 1, unit: unit, nullPasses: nulls);
    }

    /// <summary>One histogram statistic aggregated across passes, or <c>n/a</c> when no pass published one.</summary>
    private static string Latency(CampaignModel campaign, string rowId, string statistic)
    {
        var values = new List<double>();
        foreach (var passId in campaign.PassIds)
        {
            if (campaign.InPass(passId, rowId) is not { } row)
            {
                continue;
            }

            if (ArmAccess.Latency(row, Arm, "tcp-rtt", statistic).Value is { } value)
            {
                values.Add(value);
            }
        }

        return values.Count > 0 ? DescriptiveStats.FmtStat(values, digits: 1, unit: " us") : Unavailable;
    }
}
