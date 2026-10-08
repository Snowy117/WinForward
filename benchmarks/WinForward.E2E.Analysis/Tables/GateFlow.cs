using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>One gate check's outcome: whether it held, failed, could not be decided, or is a disclosure.</summary>
internal enum CheckState
{
    /// <summary>The check held.</summary>
    Pass,

    /// <summary>The check did not hold, and the row fails.</summary>
    Fail,

    /// <summary>The check could not be decided, and the row is reported as <c>n/a</c>.</summary>
    Unknown,

    /// <summary>The check is a disclosure and does not fail the row.</summary>
    Warn,
}

/// <summary>One gate check: its name, its outcome, and what it compared.</summary>
/// <param name="Name">The column the check belongs to.</param>
/// <param name="State">Whether it held.</param>
/// <param name="Detail">The numbers it compared, which is what the verdict's own note quotes.</param>
internal sealed record GateCheck(string Name, CheckState State, string Detail);

/// <summary>
/// §3.1, the flow gates: what the client says it opened, what the target's own truth file counted, and
/// whether the row's UDP carriage is the one its profile declares.
/// </summary>
/// <remarks>
/// <para><b>The TCP gate is the exact one.</b> Both the denominator and the numerator are printed, never
/// just the ratio, because a ratio slightly under one has an explanation a reader has to be able to
/// check.</para>
/// <para><b>The UDP gate is a presence check.</b> The flow count is not comparable across products, so a
/// row whose product cannot carry UDP is exempt rather than failed.</para>
/// </remarks>
internal static class GateFlow
{
    /// <summary>How much of the client's own TCP attempt count the proxy must have seen.</summary>
    internal const double TcpGateMin = 0.95;

    /// <summary>Every row of §3.1, pass by pass.</summary>
    internal static List<IReadOnlyList<string>> Rows(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var rows = new List<IReadOnlyList<string>>();
        foreach (var passId in campaign.PassIds)
        {
            foreach (var row in campaign.InPass(passId))
            {
                rows.Add(For(passId, row));
            }
        }

        return rows;
    }

    /// <summary>One (pass, row) flow-gate row, in the reference's column order.</summary>
    internal static IReadOnlyList<string> For(string passId, ClientRun row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var truth = row.ProxyTruth;
        var model = FlowModels.For(row);
        var truthTcp = JsonValue.Number(truth, "tcp");
        var truthNative = JsonValue.Number(truth, "udp") ?? 0.0;
        var truthUtcp = JsonValue.Number(truth, "utcp") ?? 0.0;
        var truthUdpTotal = truthNative + truthUtcp;
        var truthReason = TruthReason(truth, truthTcp, truthNative, truthUtcp);
        var (tcpGate, tcpRatio, adjustNote) = TcpGate(model, truthTcp, truthUtcp, truthReason);
        var (udpGate, udpRatio) = UdpGate(model, truthUdpTotal, truthReason);

        var carriage = truth is not null ? ObservedCarriage(truthNative, truthUtcp) : "n/a";
        var (expected, expectedWhy) = ExpectedCarriage(row.RunId);
        var profile = RowProfiles.Find(row.RunId);
        var notes = Notes(row, model, tcpRatio, udpRatio, truthNative, truthUtcp, adjustNote);
        var checks = Checks(row, profile, tcpRatio, tcpGate, udpRatio, udpGate, carriage, expected, expectedWhy, notes);
        var verdict = Verdict(checks, notes);

        return
        [
            passId,
            row.RunId,
            Presence(row),
            Int(model.TcpAttempts),
            VerbatimNumber.Cell(truthTcp, 0),
            tcpGate,
            Int(model.UdpArms),
            VerbatimNumber.Cell(truthNative, 0),
            VerbatimNumber.Cell(truthUtcp, 0),
            udpGate,
            carriage,
            expected ?? "unstated",
            profile is not null ? RowProfiles.Udp53Label(profile.Udp53) : "n/a",
            CheckVerdict(checks),
            verdict,
            notes.Count > 0 ? string.Join("; ", notes) : "—",
        ];
    }

    /// <summary>How much of the row's product sampling was readable, as the presence cell.</summary>
    private static string Presence(ClientRun row)
    {
        var candidates = RunSamples.Product(row);
        if (candidates.Count == 0)
        {
            return "n/a (no product process sampled)";
        }

        var readable = candidates.Count(sample => RunSamples.IsReadable(sample) && JsonValue.Number(sample, "matched") is > 0.0);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{VerbatimNumber.Fixed(100.0 * readable / candidates.Count, 0)}% ({readable}/{candidates.Count})");
    }

    private static string? TruthReason(JsonElement? truth, double? truthTcp, double? truthNative, double? truthUtcp)
    {
        if (truth is null)
        {
            return "no proxy-truth.json";
        }

        return truthTcp is null || (truthNative is null && truthUtcp is null)
            ? "proxy-truth.json has no usable tcp/udp/utcp"
            : null;
    }

    private static (string Gate, double? Ratio, string? AdjustNote) TcpGate(
        FlowModel model,
        double? truthTcp,
        double truthUtcp,
        string? truthReason)
    {
        var effective = truthTcp;
        string? adjustNote = null;
        if (truthTcp is not null && truthUtcp > 0.0 && model.TcpAttempts > 0.0)
        {
            var excess = truthTcp.Value - model.TcpAttempts;
            if (excess >= Math.Max(2.0, 0.5 * truthUtcp))
            {
                effective = truthTcp.Value - truthUtcp;
                adjustNote = $"proxy-truth.tcp exceeds the client's own attempts by {VerbatimNumber.Fixed(excess, 0)} with utcp="
                    + $"{VerbatimNumber.Fixed(truthUtcp, 0)}, which looks like an orchestrator that still counts the UoT "
                    + $"control CONNECT; the gate uses {VerbatimNumber.Fixed(truthTcp.Value, 0)} - {VerbatimNumber.Fixed(truthUtcp, 0)} "
                    + $"= {VerbatimNumber.Fixed(effective.Value, 0)}";
            }
        }

        if (truthReason is not null)
        {
            return ($"n/a ({truthReason})", null, adjustNote);
        }

        if (effective is null || model.TcpAttempts <= 0.0)
        {
            return ("n/a (no client-side TCP connection counters)", null, adjustNote);
        }

        var ratio = effective.Value / model.TcpAttempts;
        return (VerbatimNumber.Fixed(ratio, 4), ratio, adjustNote);
    }

    private static (string Gate, double? Ratio) UdpGate(FlowModel model, double truthUdpTotal, string? truthReason)
    {
        if (truthReason is not null)
        {
            return ($"n/a ({truthReason})", null);
        }

        if (model.UdpArms <= 0.0)
        {
            return ("n/a (no UDP-carrying arm)", null);
        }

        if (truthUdpTotal <= 0.0)
        {
            return ("0 flows", 0.0);
        }

        return (
            $"{Int(truthUdpTotal)} flow(s) over {Int(model.UdpArms)} UDP-carrying arm(s)",
            truthUdpTotal / model.UdpArms);
    }

    private static List<string> Notes(
        ClientRun row,
        FlowModel model,
        double? tcpRatio,
        double? udpRatio,
        double truthNative,
        double truthUtcp,
        string? adjustNote)
    {
        var notes = new List<string>();
        var control = CampaignQueries.IsControl(row.RunId);
        if (model.Missing.Count > 0 && !control)
        {
            notes.Add("denominator gaps: " + string.Join(
                "; ",
                model.Missing.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
        }

        if (!control && tcpRatio is null or < TcpGateMin)
        {
            notes.Add("tcpAttempts terms: " + model.TcpFormula());
        }

        if (!RowProfiles.UdpIncapableRows.Contains(row.RunId) && !control && udpRatio == 0.0)
        {
            notes.Add("udpArms counted: " + model.UdpFormula());
        }

        if (adjustNote is not null)
        {
            notes.Add(adjustNote);
        }

        if (truthNative > 0.0 && truthUtcp > 0.0)
        {
            notes.Add($"both UDP carriages observed (native={VerbatimNumber.Fixed(truthNative, 0)}, utcp={VerbatimNumber.Fixed(truthUtcp, 0)})");
        }

        if (row.LoadErrors.Count > 0)
        {
            notes.Add(string.Join("; ", row.LoadErrors));
        }

        if (JsonValue.Truthy(row.Document, Contracts.ArmKeys.Run.Failed))
        {
            notes.Add("harness marked this run failed");
        }

        var badLines = row.Arms.All.Sum(arm => arm.BadLines);
        if (badLines > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{badLines} unparsable JSONL line(s)"));
        }

        return notes;
    }

    private static List<GateCheck> Checks(
        ClientRun row,
        RowProfile? profile,
        double? tcpRatio,
        string tcpGate,
        double? udpRatio,
        string udpGate,
        string carriage,
        string? expected,
        string expectedWhy,
        List<string> notes)
    {
        var checks = new List<GateCheck>();
        if (CampaignQueries.IsControl(row.RunId))
        {
            notes.Add("control block: flow gates exempt (nothing is loaded); the BASE arm supplies the path-loss floor");
            checks.Add(new GateCheck("tcpGate", CheckState.Pass, "control block is exempt"));
            checks.Add(new GateCheck("udpGate", CheckState.Pass, "control block is exempt"));
            checks.Add(new GateCheck("udpCarriage", CheckState.Pass, "control block is exempt"));
            return checks;
        }

        if (tcpRatio is null)
        {
            checks.Add(new GateCheck("tcpGate", CheckState.Unknown, tcpGate));
        }
        else
        {
            checks.Add(new GateCheck(
                "tcpGate",
                tcpRatio >= TcpGateMin ? CheckState.Pass : CheckState.Fail,
                $"{VerbatimNumber.Fixed(tcpRatio.Value, 4)} >= {VerbatimNumber.Fixed(TcpGateMin, 2)}"));
        }

        if (RowProfiles.UdpIncapableRows.Contains(row.RunId))
        {
            checks.Add(new GateCheck("udpGate", CheckState.Pass, "this product cannot proxy UDP"));
        }
        else if (udpRatio is null)
        {
            checks.Add(new GateCheck("udpGate", CheckState.Unknown, udpGate));
        }
        else
        {
            checks.Add(new GateCheck("udpGate", udpRatio <= 0.0 ? CheckState.Fail : CheckState.Pass, udpGate));
        }

        if (expected is null || string.Equals(carriage, "n/a", StringComparison.Ordinal))
        {
            checks.Add(new GateCheck("udpCarriage", CheckState.Unknown, $"cannot tell (expected {expected ?? "unstated"})"));
        }
        else
        {
            checks.Add(new GateCheck(
                "udpCarriage",
                string.Equals(carriage, expected, StringComparison.Ordinal) ? CheckState.Pass : CheckState.Fail,
                $"observed {carriage}, expected {expected} ({expectedWhy})"));
        }

        if (profile?.Udp53 is RowProfiles.Udp53LocalTarget or RowProfiles.Udp53Hardcoded)
        {
            checks.Add(new GateCheck(
                "udp53Carriage",
                CheckState.Warn,
                $"direct-path measurement: {RowProfiles.Udp53Label(profile.Udp53)}"));
        }

        return checks;
    }

    /// <summary>The verdict the flow-gate checks themselves produce, before the notes are added.</summary>
    private static string CheckVerdict(IReadOnlyList<GateCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        if (checks.Any(check => check.State == CheckState.Fail))
        {
            return "FAIL";
        }

        return checks.Any(check => check.State == CheckState.Unknown) ? "n/a" : "PASS";
    }

    /// <summary>The row's verdict, with the failed or undecidable checks quoted into the notes.</summary>
    internal static string Verdict(List<GateCheck> checks, List<string> notes)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(notes);

        var failed = checks.Where(check => check.State == CheckState.Fail).ToList();
        var unknown = checks.Where(check => check.State == CheckState.Unknown).ToList();
        if (failed.Count > 0)
        {
            notes.Add("failed: " + string.Join(", ", failed.Select(check => $"{check.Name} ({check.Detail})")));
            return "FAIL";
        }

        if (unknown.Count > 0)
        {
            notes.Add("undecidable: " + string.Join(", ", unknown.Select(check => $"{check.Name} ({check.Detail})")));
            return "n/a";
        }

        return "PASS";
    }

    /// <summary>The carriage the target's flow counts show.</summary>
    private static string ObservedCarriage(double nativeFlows, double utcpFlows)
    {
        if (utcpFlows > 0.0)
        {
            return "utcp";
        }

        return nativeFlows > 0.0 ? "native" : "none";
    }

    /// <summary>The carriage the row's own profile declares, and why.</summary>
    private static (string? Expected, string Why) ExpectedCarriage(string rowId)
    {
        ArgumentNullException.ThrowIfNull(rowId);

        var profile = RowProfiles.Find(rowId);
        if (profile is null)
        {
            return (null, "row id is not in the design table");
        }

        if (string.Equals(profile.Udp, RowProfiles.UdpNotCarried, StringComparison.Ordinal))
        {
            return ("none", "the product cannot proxy UDP");
        }

        if (string.Equals(profile.Udp, RowProfiles.UdpProxiedUtcp, StringComparison.Ordinal))
        {
            return ("utcp", "the row runs UDP-over-TCP v2");
        }

        return string.Equals(profile.Udp, RowProfiles.UdpProxiedNative, StringComparison.Ordinal)
            ? ("native", "the row runs the native UDP relay")
            : (null, "row does not declare a UDP carriage");
    }

    /// <summary>Python's <c>%d</c> on a double: the integer part, toward zero.</summary>
    private static string Int(double value) => ((long)Math.Truncate(value)).ToString(CultureInfo.InvariantCulture);
}
