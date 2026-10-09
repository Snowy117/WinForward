using System.Globalization;
using WinForward.E2E.Analysis.Findings;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// §14, "Target-ledger cross-check": the target's own account of what arrived, set beside the client's,
/// with the attribution that made each number possible.
/// </summary>
/// <remarks>
/// <para><b>The ledger is the second opinion, not the source.</b> Every count the client published stays
/// the reported number; the ledger is read against it, and the tolerances that judge a disagreement are
/// the ones the ledger findings use.</para>
/// <para><b>A count that cannot be attributed is disclosed, not merged.</b> Two runs whose windows
/// overlap and a ledger with no per-run label mean the records cannot be assigned to one of them, and
/// merging would double-count a lane.</para>
/// <para><b>Where the traffic went, what each DNS listener counted, how the target's TCP verdicts compare
/// and what it could not decode are §14.3 to §14.6</b>, in <see cref="TableLedgerCross"/>.</para>
/// </remarks>
internal static class TableLedger
{
    /// <summary>The whole section when the campaign kept no ledger at all.</summary>
    private const string Unavailable =
        "n/a (no `target-ledger.jsonl` / `ledger.jsonl` found beside the pass directories, in the raw "
        + "directory or in its parent; `--ledger PATH` overrides the search). The ledger is the independent "
        + "second opinion the campaign is designed around — the client counts what it supplied and the "
        + "target counts what arrived — so without it every arrival number above rests on the client alone.";

    /// <summary>What §14.2 prints when no arm published a count either side could check.</summary>
    private const string NoAccounting =
        "n/a (no arm in this tree published a client-side connection or datagram count)";

    /// <summary>The cell for an endpoint list nobody published.</summary>
    internal const string EndpointDash = "—";

    /// <summary>Why a decode share has no value: the ledger received nothing to divide by.</summary>
    internal const string NoDatagrams = "n/a (no datagrams received)";

    /// <summary>Why a decode share has no value: the ledger publishes no undecodable counter.</summary>
    internal const string NoUndecodableCounter = "n/a (no undecodable counter)";

    private static readonly string[] s_provenanceHeaders =
        ["pass", "ledger", "records", "record types", "labels", "attribution used", "unparsable"];

    private static readonly string[] s_accountingHeaders =
    [
        "pass",
        "run",
        "arm",
        "ledger TCP connections",
        "client connections",
        "TCP check",
        "ledger datagrams",
        "client datagrams",
        "datagram check",
        "distinct UDP source endpoints",
        "endpoints",
        "census overflow",
    ];

    /// <summary>The section's body, without its heading and ending in a newline.</summary>
    /// <param name="campaign">The loaded campaign every ledger is read from.</param>
    /// <returns>The body's text.</returns>
    internal static string RenderBody(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var views = LedgerViewsBuilder.For(campaign);
        if (!views.Available)
        {
            return string.Join('\n', Unavailable, string.Empty);
        }

        var lines = new List<string> { Intro, string.Empty };
        lines.AddRange(Provenance(views));
        lines.AddRange(Accounting(views));
        lines.AddRange(TableLedgerCross.Partition(campaign, views));
        lines.AddRange(TableLedgerCross.Dns(campaign));
        lines.AddRange(TableLedgerCross.TcpVerdicts(campaign));
        lines.AddRange(TableLedgerCross.Decode(campaign));
        lines.AddRange(TableLedgerTruncated.Truncated(campaign));
        return string.Join('\n', lines);
    }

    /// <summary>§14.1: who wrote each pass's ledger, and what let a record be attributed to a run.</summary>
    private static List<string> Provenance(LedgerViews views)
    {
        var rows = new List<IReadOnlyList<string>>(views.Passes.Count);
        foreach (var (passId, entry) in views.Passes)
        {
            rows.Add(
            [
                passId,
                string.Join(", ", entry.Paths),
                entry.Records.ToString(CultureInfo.InvariantCulture),
                string.Join(", ", entry.Types.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value.ToString(CultureInfo.InvariantCulture)}")),
                Labels(entry.Labels),
                views.Attribution.GetValueOrDefault(passId, "n/a"),
                entry.BadLines.ToString(CultureInfo.InvariantCulture),
            ]);
        }

        return
        [
            "### 14.1 Provenance and attribution",
            string.Empty,
            MarkdownTable.Render(s_provenanceHeaders, rows),
            string.Empty,
        ];
    }

    /// <summary>§14.2: every arm window the ledger and the client can both speak about.</summary>
    private static List<string> Accounting(LedgerViews views)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var (passId, entry) in views.Passes)
        {
            foreach (var arm in entry.PerArm)
            {
                if (arm.TcpConnections != 0 || !UdpSilent(arm))
                {
                    rows.Add(AccountingRow(passId, arm));
                }
            }
        }

        return
        [
            "### 14.2 Client vs target accounting, per (pass, run, arm)",
            string.Empty,
            rows.Count > 0 ? MarkdownTable.Render(s_accountingHeaders, rows) : NoAccounting,
            string.Empty,
        ];
    }

    /// <summary>One arm's row: both sides' counts and the two checks between them.</summary>
    private static List<string> AccountingRow(string passId, LedgerArmView arm)
    {
        var connections = arm.ClientConnections;
        var datagrams = arm.ClientDatagrams;
        var band = datagrams is not null && NonZero(datagrams)
            ? LedgerFindings.DatagramBand(datagrams.Value, arm.DurationSeconds)
            : (double?)null;
        var connectionCheck = DescriptiveStats.WithinTolerance(
            arm.TcpConnections,
            connections,
            LedgerFindings.ConnectionTolerance,
            LedgerFindings.ConnectionSlack);
        return
        [
            passId,
            arm.Row,
            arm.Arm,
            arm.TcpConnections.ToString(CultureInfo.InvariantCulture),
            VerbatimNumber.Cell(connections, 0),
            ConnectionCheck(connectionCheck, connections),
            DatagramCount(arm),
            VerbatimNumber.Cell(datagrams, 0),
            DatagramCheck(arm.UdpDatagrams, datagrams, band),
            arm.UdpEndpoints.Count.ToString(CultureInfo.InvariantCulture),
            Endpoints(arm.UdpEndpoints),
            arm.UdpOverflow.ToString(CultureInfo.InvariantCulture),
        ];
    }

    /// <summary>Whether an arm window published nothing at all on either side's UDP counters.</summary>
    private static bool UdpSilent(LedgerArmView arm) =>
        arm.UdpDatagrams.Equals(0.0) && !NonZero(arm.ClientConnections) && !NonZero(arm.ClientDatagrams);

    /// <summary>The window's datagram census.</summary>
    private static string DatagramCount(LedgerArmView arm) => VerbatimNumber.Json(arm.UdpDatagrams);

    private static string ConnectionCheck(bool? check, double? connections)
    {
        if (check == true)
        {
            return "ok";
        }

        return connections is null ? "n/a" : "MISMATCH";
    }

    /// <summary>The endpoints a window's census names, first three, with the rest elided.</summary>
    private static string Endpoints(IReadOnlyList<string> endpoints)
    {
        if (endpoints.Count == 0)
        {
            return EndpointDash;
        }

        var head = string.Join(", ", endpoints.Take(3));
        return endpoints.Count > 3 ? head + "…" : head;
    }

    private static string DatagramCheck(double observed, double? expected, double? band)
    {
        if (expected is null || band is null)
        {
            return "n/a";
        }

        var agrees = Math.Abs(observed - expected.Value) <= band.Value;
        return agrees
            ? $"ok (±{VerbatimNumber.Cell(band, 0)})"
            : $"MISMATCH (±{VerbatimNumber.Cell(band, 0)})";
    }

    private static string Labels(IReadOnlyDictionary<string, int> labels)
    {
        var ordered = labels.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        var text = string.Join(
            ", ",
            ordered.Take(4).Select(pair => $"{pair.Key} x{pair.Value.ToString(CultureInfo.InvariantCulture)}"));
        if (text.Length == 0)
        {
            text = "none";
        }

        return ordered.Count > 4
            ? text + $" … ({ordered.Count.ToString(CultureInfo.InvariantCulture)} labels in all)"
            : text;
    }

    /// <summary>Whether a reading is present and not exactly zero.</summary>
    private static bool NonZero(double? value) => value is not null && !value.Value.Equals(0.0);

    private const string Intro =
        "The ledger is the target's own account of what arrived, written independently of the client. Every "
        + "record carries `utc` and `label`; `udpSummary` carries `sources[{address,port,datagrams}]` and "
        + "`sourceOverflow`, `dnsSummary` carries `port`, and every TCP connection is its own `tcp` record. A "
        + "campaign can run more than one target instance — the shipped launcher starts a proxied target and a "
        + "separate direct-lane target, each with its own ledger — so every ledger found is read, and a record is "
        + "attributed to a (run, arm) by the ledger's own label where the campaign supplied one and by the arm's "
        + "UTC window otherwise. The window is derived from the client's `startedUtc` plus the arm's tick offsets, "
        + "which is the only bridge between the client's stopwatch and the ledger's wall clock. A connection that "
        + "opens inside one arm and closes inside the next is attributed where it closed. When two runs' windows "
        + "overlap and the ledger carries no per-run label, the counts are reported as unattributable rather than "
        + "silently merged, because merging would double-count a lane. The tolerances used to judge a mismatch are "
        + "declared in the source (`LEDGER_CONNECTION_*`, `LEDGER_DATAGRAM_*`).";
}
