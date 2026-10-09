using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using WinForward.E2E.Analysis.Loading;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Findings;

/// <summary>
/// Attributes the target ledger's records to the arm windows that produced them, and publishes what each
/// window's traffic looked like.
/// </summary>
/// <remarks>
/// <para><b>Attribution is by the ledger's own identity, then by label, then by the arm's own UTC
/// window.</b> A ledger that names the target instance that wrote it (the shipped launcher labels each
/// of its two long-lived instances <c>target:&lt;port&gt;</c>) is read against the runs whose own
/// <c>run.json</c> declares that instance, so a record can only be attributed to a run that could have
/// produced it; within that ledger the arm's UTC window — derived from the client's ticks — bounds it.
/// A ledger that carries per-run labels still selects by label where the campaign supplied them, which
/// is what a one-run-per-target campaign does.</para>
/// <para><b>An overlapping window is disclosed.</b> Two runs whose windows overlap <em>and that share a
/// ledger</em> cannot be told apart from the records alone, which the findings say rather than silently
/// counting them twice. Two runs that wrote to different target instances are not ambiguous however
/// much their windows overlap: the dual phase's two lanes overlap by construction and are separated by
/// exactly this rule.</para>
/// </remarks>
internal static class LedgerViewsBuilder
{
    /// <summary>The envelope member every ledger record carries.</summary>
    private const string TypeKey = "type";

    /// <summary>
    /// The prefix the shipped launcher gives a target instance's own label. The rest of the label is the
    /// port the instance serves, which is the one thing its ledgers and its clients' <c>run.json</c> both
    /// name.
    /// </summary>
    private const string InstanceLabelPrefix = "target:";

    /// <summary>The <c>udpSummary</c> record family, whose interval totals the views read.</summary>
    internal const string UdpSummary = "udpSummary";

    /// <summary>The <c>tcpSummary</c> record family, which the echo listener's own totals sit in.</summary>
    internal const string TcpSummary = "tcpSummary";

    /// <summary>The <c>dnsSummary</c> record family, which the DNS totals are read from.</summary>
    internal const string DnsSummary = "dnsSummary";

    /// <summary>The <c>targetSummary</c> record family, which the ledger's own error count sits in.</summary>
    internal const string TargetSummary = "targetSummary";

    /// <summary>The per-connection record family.</summary>
    internal const string Tcp = "tcp";

    private static readonly string[] s_udpEchoArms = ["LAT", "LATLOAD", "LOSS", "MIX", "BASE"];

    /// <summary>The <c>run.json</c> target members whose value is a port the run talked to.</summary>
    private static readonly string[] s_targetPortKeys =
        [Contracts.ArmKeys.Run.TargetObject.TcpPort, Contracts.ArmKeys.Run.TargetObject.UdpPort];

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
            var instances = InstancesOf(ledgers);
            var pools = PoolsOf(runs, instances);
            var windows = WindowsOf(runs);
            passes[passId] = new LedgerPassView
            {
                Paths = [.. ledgers.Select(ledger => ledger.Path)],
                Records = records.Count,
                Types = TypesOf(records),
                Labels = labels,
                BadLines = ledgers.Sum(ledger => ledger.BadLines),
                PerArm = ArmViews(passId, runs, records, windows, discriminating, pools),
            };
            attribution[passId] = Attribution(ledgers.Count, labels, discriminating, instances, pools);
        }

        LedgerFindings.AttachClientCounts(campaign, passes);
        return new LedgerViews { Available = available, Passes = passes, Attribution = attribution };
    }

    /// <summary>
    /// The target instance each ledger was written by, keyed by ledger path, for the ledgers that name
    /// one. A ledger names it by carrying the same <c>target:&lt;port&gt;</c> label on every record.
    /// </summary>
    private static Dictionary<string, int> InstancesOf(IReadOnlyList<LedgerData> ledgers)
    {
        var instances = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var ledger in ledgers)
        {
            if (InstancePortOf(ledger) is { } port)
            {
                instances[ledger.Path] = port;
            }
        }

        return instances;
    }

    /// <summary>The port one ledger's own label names, or null when its records do not agree on one.</summary>
    private static int? InstancePortOf(LedgerData ledger)
    {
        int? port = null;
        foreach (var record in ledger.Records)
        {
            var label = JsonValue.String(record.Payload, Contracts.ArmKeys.Ledger.Envelope.Label);
            if (InstancePort(label) is not { } candidate || (port is not null && port != candidate))
            {
                return null;
            }

            port = candidate;
        }

        return port;
    }

    /// <summary>The port a label names, or null when it is not the launcher's target-instance label.</summary>
    private static int? InstancePort(string? label)
    {
        if (label is null || !label.StartsWith(InstanceLabelPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return int.TryParse(
                label[InstanceLabelPrefix.Length..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var port)
            && port is > 0 and <= 65535
                ? port
                : null;
    }

    /// <summary>
    /// The ledgers each run's traffic could have reached: the instances its own <c>run.json</c> names,
    /// when the ledgers name one. Null means "unknown, read every ledger", which is what a campaign whose
    /// ledgers carry no instance label gets.
    /// </summary>
    private static Dictionary<string, HashSet<string>?> PoolsOf(
        List<(ClientRun Run, ClientRun Owner)> runs,
        Dictionary<string, int> instances)
    {
        var pools = new Dictionary<string, HashSet<string>?>(StringComparer.Ordinal);
        foreach (var (run, _) in runs)
        {
            pools[run.RunId] = Pool(run, instances);
        }

        return pools;
    }

    private static HashSet<string>? Pool(ClientRun run, Dictionary<string, int> instances)
    {
        HashSet<string>? pool = null;
        foreach (var port in TargetPorts(run))
        {
            foreach (var (path, instancePort) in instances)
            {
                if (instancePort == port)
                {
                    pool ??= new HashSet<string>(StringComparer.Ordinal);
                    pool.Add(path);
                }
            }
        }

        return pool;
    }

    /// <summary>The ports the run's own <c>run.json</c> says it talked to.</summary>
    private static List<int> TargetPorts(ClientRun run)
    {
        var ports = new List<int>(s_targetPortKeys.Length);
        foreach (var name in s_targetPortKeys)
        {
            var path = Contracts.ArmKeys.Run.Target + JsonValue.Separator + name;
            if (JsonValue.AsNumber(JsonValue.Dig(run.Document, path)) is > 0 and <= 65535 and var port)
            {
                ports.Add((int)port);
            }
        }

        return ports;
    }

    /// <summary>Whether two runs' windows can be told apart by the ledgers they wrote to.</summary>
    private static bool SharesLedger(string left, string right, Dictionary<string, HashSet<string>?> pools)
    {
        var leftPool = pools.GetValueOrDefault(left);
        var rightPool = pools.GetValueOrDefault(right);
        return leftPool is null || rightPool is null || leftPool.Overlaps(rightPool);
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

    private static Dictionary<string, int> TypesOf(IReadOnlyList<LedgerRecord> records) =>
        records
            .GroupBy(
                record => JsonText.Of(JsonValue.Member(record.Payload, TypeKey)),
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
        bool discriminating,
        Dictionary<string, HashSet<string>?> pools)
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

                perArm.Add(ArmView(passId, run, owner, armName, window, records, windows, discriminating, pools));
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
        bool discriminating,
        Dictionary<string, HashSet<string>?> pools)
    {
        var label = RunClocks.Label(run);
        var pool = pools.GetValueOrDefault(run.RunId);
        var selected = records
            .Where(record => (pool is null || pool.Contains(record.LedgerPath))
                && record.Utc is not null && window.Start <= record.Utc && record.Utc <= window.End)
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
                && SharesLedger(run.RunId, other.Key, pools)
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

    private static string Attribution(
        int ledgerCount,
        Dictionary<string, int> labels,
        bool discriminating,
        Dictionary<string, int> instances,
        Dictionary<string, HashSet<string>?> pools)
    {
        var sorted = string.Join(", ", labels.Keys.Order(StringComparer.Ordinal));
        if (discriminating)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{ledgerCount} ledger(s); the ledger's own label ({sorted}) selects the run, then the arm's UTC window bounds it");
        }

        if (instances.Count > 0)
        {
            var said = string.Join(
                ", ",
                instances.Values.Distinct().Order().Select(port => InstanceLabelPrefix + port.ToString(CultureInfo.InvariantCulture)));
            const string names = "the ledger's own label names the target instance that wrote it";
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{ledgerCount} ledger(s); {names} ({said}), {Reach(pools)}");
        }

        var carries = labels.Count == 1 ? "one label" : "no label";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{ledgerCount} ledger(s); the arm's UTC window only, because the ledger carries {carries}, so a record's own label cannot select a row");
    }

    /// <summary>
    /// How far an instance label actually binds the pass's runs, which is the difference between the
    /// rule being available and a run declaring the instance that wrote the ledger it read.
    /// </summary>
    private static string Reach(Dictionary<string, HashSet<string>?> pools)
    {
        if (!pools.Values.Any(pool => pool is not null))
        {
            return "but no run's own run.json declares one of them, so the arm's UTC window only, "
                + "because a record's own label cannot select a run";
        }

        return pools.Values.All(pool => pool is not null)
            ? "so a run is read against the ledger of the target its own run.json declares, then the arm's UTC window bounds it"
            : "so a run whose own run.json declares one is read against that target's ledger and a run that declares none is read against every ledger, then the arm's UTC window bounds the arm";
    }
}
