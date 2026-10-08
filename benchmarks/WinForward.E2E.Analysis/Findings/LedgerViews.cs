using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using WinForward.E2E.Analysis.Loading;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Findings;

/// <summary>One arm's window as the ledger sees it: what the ledger recorded, and what the client claims.</summary>
internal sealed class LedgerArmView
{
    /// <summary>The pass and the run whose window this is.</summary>
    internal required string Pass { get; init; }

    /// <summary>The run id the window belongs to.</summary>
    internal required string Row { get; init; }

    /// <summary>The row that owns the run; a lane's owner is its row.</summary>
    internal required string Owner { get; init; }

    /// <summary>The arm.</summary>
    internal required string Arm { get; init; }

    /// <summary>Other runs whose windows overlap this one.</summary>
    internal required IReadOnlyList<string> OverlappingRuns { get; init; }

    /// <summary>Whether the overlap makes the window's records unattributable.</summary>
    internal required bool Unattributable { get; init; }

    /// <summary>How many TCP connections the ledger saw in the window.</summary>
    internal required int TcpConnections { get; init; }

    /// <summary>How many datagrams the window's endpoint census accounts for.</summary>
    internal required double UdpDatagrams { get; init; }

    /// <summary>The endpoints the window's census names.</summary>
    internal required IReadOnlyList<string> UdpEndpoints { get; init; }

    /// <summary>The endpoint census table's overflow count.</summary>
    internal required int UdpOverflow { get; init; }

    /// <summary>What the client says it sent, or null when its own counters do not cover the arm.</summary>
    internal double? ClientConnections { get; set; }

    /// <summary>Where the client's connection count comes from.</summary>
    internal string? ClientConnectionsSource { get; set; }

    /// <summary>What the client says it sent as datagrams, or null.</summary>
    internal double? ClientDatagrams { get; set; }

    /// <summary>Where the client's datagram count comes from.</summary>
    internal string? ClientDatagramsSource { get; set; }

    /// <summary>How long the arm ran, which sets the datagram tolerance's floor.</summary>
    internal double DurationSeconds { get; set; }
}

/// <summary>One pass's ledger, attributed arm by arm.</summary>
internal sealed class LedgerPassView
{
    /// <summary>How many records the pass's ledgers hold.</summary>
    internal required int Records { get; init; }

    /// <summary>How many lines were not JSON.</summary>
    internal required int BadLines { get; init; }

    /// <summary>One entry per arm window.</summary>
    internal required IReadOnlyList<LedgerArmView> PerArm { get; init; }

}

/// <summary>The campaign's ledgers, read once.</summary>
internal sealed class LedgerViews
{
    /// <summary>Whether any pass has a ledger at all.</summary>
    internal required bool Available { get; init; }

    /// <summary>One view per pass that has a ledger.</summary>
    internal required IReadOnlyDictionary<string, LedgerPassView> Passes { get; init; }

    /// <summary>Per pass, how a record is attributed to a run, in the reference's words.</summary>
    internal required IReadOnlyDictionary<string, string> Attribution { get; init; }
}

/// <summary>
/// Attributes the target ledger's records to the arm windows that produced them, and publishes what each
/// window's traffic looked like.
/// </summary>
/// <remarks>
/// <para><b>Attribution is by label, then by the arm's own UTC window.</b> The ledger's label selects the
/// run when the ledger carries more than one; the window — derived from the client's ticks — bounds it
/// either way, because a label alone cannot say when.</para>
/// <para><b>An overlapping window is disclosed.</b> Two runs whose windows overlap and a ledger with no
/// per-run label means the records cannot be attributed to one of them, which the findings say rather
/// than silently counting them twice.</para>
/// </remarks>
internal static class LedgerViewsBuilder
{
    /// <summary>The envelope member every ledger record carries.</summary>
    private const string TypeKey = "type";

    /// <summary>The <c>udpSummary</c> record family, whose interval totals the views read.</summary>
    private const string UdpSummary = "udpSummary";

    /// <summary>The <c>dnsSummary</c> record family, which the DNS totals are read from.</summary>
    internal const string DnsSummary = "dnsSummary";

    /// <summary>The <c>targetSummary</c> record family, which the ledger's own error count sits in.</summary>
    internal const string TargetSummary = "targetSummary";

    /// <summary>The per-connection record family.</summary>
    private const string Tcp = "tcp";

    private static readonly string[] s_udpEchoArms = ["LAT", "LATLOAD", "LOSS", "MIX", "BASE"];

    private static readonly ConditionalWeakTable<CampaignModel, LedgerViews> s_cache = [];

    /// <summary>The campaign's ledger views, computed once per campaign.</summary>
    internal static LedgerViews For(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        return s_cache.GetValue(campaign, Build);
    }

    /// <summary>The arms whose datagrams reach the UDP echo port, which the endpoint partition reads.</summary>
    internal static IReadOnlyList<string> UdpEchoArms => s_udpEchoArms;

    private static LedgerViews Build(CampaignModel campaign)
    {
        var passes = new Dictionary<string, LedgerPassView>(StringComparer.Ordinal);
        var attribution = new Dictionary<string, string>(StringComparer.Ordinal);
        var available = false;
        foreach (var passId in campaign.PassIds)
        {
            var ledgers = LedgerLoader.For(campaign, passId);
            if (ledgers.Count == 0)
            {
                continue;
            }

            available = true;
            var records = RecordsOf(ledgers);
            var labels = LabelsOf(records);
            var runs = RunsOf(campaign, passId);
            var discriminating = labels.Count > 1 && labels.Keys.Any(RunLabels(runs).Contains);
            var windows = WindowsOf(runs);
            passes[passId] = new LedgerPassView
            {
                Records = records.Count,
                BadLines = ledgers.Sum(ledger => ledger.BadLines),
                PerArm = ArmViews(passId, runs, records, windows, discriminating),
            };
            attribution[passId] = Attribution(ledgers.Count, labels, discriminating);
        }

        LedgerFindings.AttachClientCounts(campaign, passes);
        return new LedgerViews { Available = available, Passes = passes, Attribution = attribution };
    }

    private static List<LedgerRecord> RecordsOf(IReadOnlyList<LedgerData> ledgers)
    {
        var records = new List<LedgerRecord>();
        foreach (var ledger in ledgers)
        {
            var previousReceived = 0.0;
            foreach (var record in ledger.Records)
            {
                var receivedDelta = 0.0;
                if (string.Equals(JsonValue.String(record.Payload, TypeKey), UdpSummary, StringComparison.Ordinal))
                {
                    var received = JsonValue.Number(record.Payload, Contracts.ArmKeys.Ledger.UdpSummary.Received) ?? 0.0;
                    receivedDelta = Math.Max(0.0, received - previousReceived);
                    previousReceived = received;
                }

                records.Add(record with { ReceivedDelta = receivedDelta });
            }
        }

        return records;
    }

    private static Dictionary<string, int> LabelsOf(IReadOnlyList<LedgerRecord> records) =>
        records
            .GroupBy(
                record => JsonText.Of(JsonValue.Member(record.Payload, Contracts.ArmKeys.Ledger.Envelope.Label)),
                StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static List<(ClientRun Run, ClientRun Owner)> RunsOf(CampaignModel campaign, string passId)
    {
        var runs = new List<(ClientRun Run, ClientRun Owner)>();
        foreach (var row in campaign.InPass(passId))
        {
            runs.Add((row, row));
            if (row.DualProxied is not null)
            {
                runs.Add((row.DualProxied, row));
            }

            if (row.DualDirect is not null)
            {
                runs.Add((row.DualDirect, row));
            }
        }

        return runs;
    }

    private static HashSet<string> RunLabels(List<(ClientRun Run, ClientRun Owner)> runs) =>
        runs.Where(entry => !string.IsNullOrEmpty(RunClocks.Label(entry.Run)))
            .Select(entry => RunClocks.Label(entry.Run)!)
            .ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, List<(string Arm, DateTimeOffset Start, DateTimeOffset End)>> WindowsOf(
        List<(ClientRun Run, ClientRun Owner)> runs)
    {
        var windows = new Dictionary<string, List<(string Arm, DateTimeOffset Start, DateTimeOffset End)>>(StringComparer.Ordinal);
        foreach (var run in runs.Select(entry => entry.Run))
        {
            foreach (var armName in ArmRecords.LoadOrder)
            {
                if (RunClocks.ArmUtcWindow(run, armName) is not { } window)
                {
                    continue;
                }

                if (!windows.TryGetValue(run.RunId, out var spans))
                {
                    spans = [];
                    windows[run.RunId] = spans;
                }

                spans.Add((armName, window.Start, window.End));
            }
        }

        return windows;
    }

    private static List<LedgerArmView> ArmViews(
        string passId,
        List<(ClientRun Run, ClientRun Owner)> runs,
        List<LedgerRecord> records,
        Dictionary<string, List<(string Arm, DateTimeOffset Start, DateTimeOffset End)>> windows,
        bool discriminating)
    {
        var perArm = new List<LedgerArmView>();
        foreach (var (run, owner) in runs)
        {
            foreach (var armName in ArmRecords.LoadOrder)
            {
                if (!run.Arms.Contains(armName) || RunClocks.ArmUtcWindow(run, armName) is not { } window)
                {
                    continue;
                }

                perArm.Add(ArmView(passId, run, owner, armName, window, records, windows, discriminating));
            }
        }

        return perArm;
    }

    private static LedgerArmView ArmView(
        string passId,
        ClientRun run,
        ClientRun owner,
        string armName,
        (DateTimeOffset Start, DateTimeOffset End) window,
        List<LedgerRecord> records,
        Dictionary<string, List<(string Arm, DateTimeOffset Start, DateTimeOffset End)>> windows,
        bool discriminating)
    {
        var label = RunClocks.Label(run);
        var selected = records
            .Where(record => record.Utc is not null && window.Start <= record.Utc && record.Utc <= window.End)
            .ToList();
        if (discriminating && label is not null)
        {
            selected = [.. selected.Where(record => string.Equals(
                JsonValue.String(record.Payload, Contracts.ArmKeys.Ledger.Envelope.Label),
                label,
                StringComparison.Ordinal))];
        }

        var overlap = windows
            .Where(other => !string.Equals(other.Key, run.RunId, StringComparison.Ordinal)
                && other.Value.Exists(span => span.Start <= window.End && window.Start <= span.End))
            .Select(other => other.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        var udpRecords = selected
            .Where(record => string.Equals(JsonValue.String(record.Payload, TypeKey), UdpSummary, StringComparison.Ordinal))
            .ToList();
        var tcpRecords = selected
            .Where(record => string.Equals(JsonValue.String(record.Payload, TypeKey), Tcp, StringComparison.Ordinal))
            .ToList();
        var endpoints = Endpoints(udpRecords);
        return new LedgerArmView
        {
            Pass = passId,
            Row = run.RunId,
            Owner = owner.RunId,
            Arm = armName,
            OverlappingRuns = overlap,
            Unattributable = overlap.Count > 0 && !discriminating,
            TcpConnections = tcpRecords.Count,
            UdpDatagrams = endpoints.Values.Sum(),
            UdpEndpoints = [.. endpoints.Keys.Order(StringComparer.Ordinal)],
            UdpOverflow = udpRecords.Sum(record => (int)Math.Truncate(
                JsonValue.Number(record.Payload, Contracts.ArmKeys.Ledger.UdpSummary.SourceOverflow) ?? 0.0)),
        };
    }

    private static Dictionary<string, double> Endpoints(List<LedgerRecord> udpRecords)
    {
        var endpoints = new Dictionary<string, double>(StringComparer.Ordinal);
        var sources = udpRecords
            .SelectMany(record => JsonValue.Array(record.Payload, Contracts.ArmKeys.Ledger.UdpSummary.Sources) ?? [])
            .Where(source => source.ValueKind == JsonValueKind.Object);
        foreach (var source in sources)
        {
            var key = $"{JsonText.Of(JsonValue.Member(source, Contracts.ArmKeys.Ledger.UdpSummary.SourceEntry.Address))}:"
                + $"{JsonText.Of(JsonValue.Member(source, Contracts.ArmKeys.Ledger.UdpSummary.SourceEntry.Port))}";
            endpoints[key] = endpoints.GetValueOrDefault(key)
                + (JsonValue.Number(source, Contracts.ArmKeys.Ledger.UdpSummary.SourceEntry.Datagrams) ?? 0.0);
        }

        return endpoints;
    }

    private static string Attribution(int ledgerCount, Dictionary<string, int> labels, bool discriminating)
    {
        var sorted = string.Join(", ", labels.Keys.Order(StringComparer.Ordinal));
        if (discriminating)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{ledgerCount} ledger(s); the ledger's own label ({sorted}) selects the run, then the arm's UTC window bounds it");
        }

        var carries = labels.Count == 1 ? "one label" : "no label";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{ledgerCount} ledger(s); the arm's UTC window only, because the ledger carries {carries}, so a record's own label cannot select a row");
    }
}
