using System.Globalization;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Metrics;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// "TCP reliability detail": one line per row, the <c>REL</c> arm's outcome distribution, its two
/// surprise rates, its attempt counts, and whether the arm ended with work in flight.
/// </summary>
/// <remarks>
/// <para><b>Every rate divides by connect attempts, never by completed connections.</b> Dividing by the
/// connections that succeeded would flatter a product that failed to connect at all: the failures are
/// the denominator's whole point.</para>
/// <para><b>The rule of three replaces a zero with a bound.</b> A row that observed no timeouts did not
/// measure a zero rate; it bounded the rate near <c>3/attempts</c>, and the cell says so with the
/// attempt count it was bounded against.</para>
/// <para><b>The in-flight column is the record's own caveat.</b> <c>connectAttempts</c> and
/// <c>scheduledAttempts</c> disagree exactly when the arm was torn down with work outstanding, and a row
/// whose counts disagree must not be compared against another without saying so.</para>
/// </remarks>
internal static class TableTcp
{
    private const string Arm = "REL";

    private const string NoResult = "n/a (no REL result)";

    private const string NotPublished = "n/a (not published)";

    private const string CountsAgree = "connectAttempts == scheduledAttempts";

    private const string Unavailable = "n/a";

    private static readonly string[] s_outcomes =
    [
        "clean",
        "reset",
        "unexpectedEof",
        "timeout",
        "connectFail",
        "halfCloseViolation",
        "otherError",
    ];

    private static readonly string[] s_headers =
    [
        "row",
        "passes",
        .. s_outcomes.Select(key => $"{key} %"),
        "unexpectedEofRate %",
        "fidelityRate %",
        "connectAttempts",
        "scheduledAttempts",
        "in-flight check",
        "meanConnectMs",
        "meanTransferMs",
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
            "From the `REL` arm. Outcome rates divide by `metrics.connectAttempts` (never by completed connections) and "
            + "are printed as percentages. `unexpectedEofRate = metrics.unexpectedEof / connectAttempts` and `fidelityRate = "
            + "metrics.fidelityMismatch / connectAttempts`; the same definitions drive the headline matrix and verdict.json. "
            + "`unexpectedEof` here excludes the expected early EOFs that `modeSchedule` deliberately provokes "
            + "(`expectedEarlyEof`), so it is normally smaller than `outcomes.unexpectedEof`. `connectAttempts == "
            + "scheduledAttempts` is an invariant: a mismatch means the arm ended with work in flight, and the record must "
            + "not be compared against another without saying so — the last column says it. Rule of three applies to zero "
            + "rates. `meanConnectMs` is the mean connect duration over the attempts that connected and `meanTransferMs` the "
            + "mean duration of the request send alone, over the attempts that completed one; each is `null` — printed `n/a` "
            + "— when it has no sample, never a zero. Where the outcome marginals leave a question open, the arm's raw "
            + "record also carries `metrics.byMode` (the joint mode × observed distribution with each mode's echoed and "
            + "trailer bytes) and one `type: \"attempt\"` line per disconfirming attempt, joining a client observation to the "
            + "target ledger's verdict through the same `connectionId`. Every cell is `median [p25–p75] across passes "
            + "(n=K)`.",
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
            return [rowId, "0", $"n/a ({reason})", .. Absent(s_headers.Length - 3)];
        }

        var arm = Read(campaign, rowId);
        return arm.Passes == 0 ? [rowId, "0", NoResult, .. Absent(s_headers.Length - 3)] : Cells(rowId, arm);
    }

    /// <summary>One row's cells: every rate bounded by the row's own median attempt count.</summary>
    private static List<string> Cells(string rowId, RelReading arm)
    {
        var bound = arm.Attempts.Count > 0
            ? (int)Math.Round(DescriptiveStats.Median(arm.Attempts)!.Value, MidpointRounding.ToEven)
            : (int?)null;
        var rendered = new List<string>(s_headers.Length)
        {
            rowId,
            arm.Passes.ToString(CultureInfo.InvariantCulture),
        };

        foreach (var outcome in s_outcomes)
        {
            rendered.Add(Rate(arm.Rates[outcome]));
        }

        rendered.Add(Rate(arm.Unexpected));
        rendered.Add(Rate(arm.Fidelity));
        rendered.Add(arm.Attempts.Count > 0 ? DescriptiveStats.FmtStat(arm.Attempts, 1) : Unavailable);
        rendered.Add(arm.Scheduled.Count > 0 ? DescriptiveStats.FmtStat(arm.Scheduled, 1) : NotPublished);
        rendered.Add(arm.InFlight.Count > 0 ? string.Join("; ", arm.InFlight) : CountsAgree);
        rendered.Add(arm.MeanConnect.Count > 0 ? DescriptiveStats.FmtStat(arm.MeanConnect, 3, " ms") : Unavailable);
        rendered.Add(arm.MeanTransfer.Count > 0 ? DescriptiveStats.FmtStat(arm.MeanTransfer, 3, " ms") : Unavailable);
        return rendered;

        string Rate(List<double> values) =>
            values.Count > 0 ? DescriptiveStats.FmtStat(values, 4, " %", bound, 100.0) : Unavailable;
    }

    /// <summary>Every pass's contribution to one row's line.</summary>
    private static RelReading Read(CampaignModel campaign, string rowId)
    {
        var reading = new RelReading();
        foreach (var passId in campaign.PassIds)
        {
            if (campaign.InPass(passId, rowId) is { } row)
            {
                Pass(reading, row, passId);
            }
        }

        return reading;
    }

    /// <summary>One pass of one row: the outcome marginals, the two rates and the counts they divide by.</summary>
    private static void Pass(RelReading reading, ClientRun row, string passId)
    {
        if (ArmAccess.Number(row, Arm, "metrics/connectAttempts").Value is not { } attempts)
        {
            return;
        }

        reading.Passes++;
        reading.Attempts.Add(attempts);
        var (scheduled, why) = ArmAccess.Number(row, Arm, "metrics/scheduledAttempts");
        if (scheduled is null)
        {
            reading.InFlight.Add($"{passId}: n/a ({why})");
        }
        else
        {
            reading.Scheduled.Add(scheduled.Value);
            if (scheduled.Value.CompareTo(attempts) != 0)
            {
                reading.InFlight.Add(
                    $"{passId}: connectAttempts {VerbatimNumber.Fixed(attempts, 0)}"
                    + $" != scheduledAttempts {VerbatimNumber.Fixed(scheduled.Value, 0)}, work in flight at teardown");
            }
        }

        foreach (var outcome in s_outcomes)
        {
            if (ArmAccess.Number(row, Arm, $"metrics/outcomes/{outcome}").Value is { } value
                && attempts.CompareTo(0.0) != 0)
            {
                reading.Rates[outcome].Add(100.0 * value / attempts);
            }
        }

        if (ArmAccess.Ratio(row, Arm, "metrics/unexpectedEof", "metrics/connectAttempts").Value is { } eof)
        {
            reading.Unexpected.Add(eof * 100.0);
        }

        if (ArmAccess.Ratio(row, Arm, "metrics/fidelityMismatch", "metrics/connectAttempts").Value is { } fidelity)
        {
            reading.Fidelity.Add(fidelity * 100.0);
        }

        if (ArmAccess.Number(row, Arm, "metrics/meanConnectMs").Value is { } connect)
        {
            reading.MeanConnect.Add(connect);
        }

        if (ArmAccess.Number(row, Arm, "metrics/meanTransferMs").Value is { } transfer)
        {
            reading.MeanTransfer.Add(transfer);
        }
    }

    /// <summary>One value repeated for a row that never ran the arm.</summary>
    private static string[] Absent(int count)
    {
        var cells = new string[count];
        Array.Fill(cells, Unavailable);
        return cells;
    }

    /// <summary>Every pass's contribution to one row's line.</summary>
    private sealed class RelReading
    {
        internal int Passes { get; set; }

        internal Dictionary<string, List<double>> Rates { get; } =
            s_outcomes.ToDictionary(outcome => outcome, _ => new List<double>(), StringComparer.Ordinal);

        internal List<double> Attempts { get; } = [];

        internal List<double> Scheduled { get; } = [];

        internal List<double> Unexpected { get; } = [];

        internal List<double> Fidelity { get; } = [];

        internal List<double> MeanConnect { get; } = [];

        internal List<double> MeanTransfer { get; } = [];

        internal List<string> InFlight { get; } = [];
    }
}
