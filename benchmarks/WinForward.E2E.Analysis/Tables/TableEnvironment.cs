using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Analysis.Findings;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// §2, "Environment and provenance": what the campaign ran on and with, the declared loss window per arm,
/// how long each arm ran, which process was sampled, what each row's configuration hashed to, and each
/// run's own metadata.
/// </summary>
/// <remarks>
/// <para><b>Provenance is part of a measurement.</b> A number without the machine, the runtime, the plan
/// hash and the configuration it came from cannot be reproduced, and the ledger line says which file the
/// second opinion was read from and how its records were attributed to runs.</para>
/// <para><b>The window is declared, not derived.</b> The harness publishes <c>parameters.lossWindowMs</c>
/// and repeats it as <c>metrics.window</c> and <c>gates.windowMs</c>, so the arrived/late/never split is
/// reproducible from the record alone.</para>
/// </remarks>
internal static class TableEnvironment
{
    private const string WindowNote =
        "`window` is **the plan's declared `lossWindowMs`** (200 ms when the plan declares none). It is a "
        + "declared, published parameter, not derived from an observed latency: an earlier caption in this "
        + "analysis claimed `max(200 ms, 5 × observed p99 RTT)` capped at 2000 ms, which was **wrong** and has "
        + "been removed — the harness publishes `parameters.lossWindowMs` and repeats it as `metrics.window` "
        + "and `gates.windowMs`, so the arrived/late/never split is reproducible from the record alone. A `null` "
        + "rate in any table means its denominator was zero (nothing was sent) and is rendered as an empty "
        + "cell, never as a zero.";

    private const string SamplingNote =
        "A sample whose `readError` is true is rejected from every CPU and memory cell, because the harness "
        + "writes `null` (not `0`) for a process whose counters could not be read and a zero there is not a "
        + "measurement; a `samplerError` record means a whole sampling tick failed and is disclosed rather than "
        + "averaged over. Only the process with the most non-self samples is analysed; the campaign samples "
        + "exactly one product process per row, so a second name here means the run needs a closer look.";

    /// <summary>The section's body, without its heading and ending in a newline.</summary>
    internal static string RenderBody(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var lines = new List<string>();
        AddTable(lines, ["item", "value"], Info(campaign));
        AddHeading(lines, "Declared loss threshold (`window`, ms) per arm");
        AddTable(lines, ["arm", "field", "published value", "declared in the plan"], WindowRows(campaign));
        AddParagraph(lines, WindowNote);
        AddHeading(lines, "Arm durations (s, from each run's own tick deltas)");
        AddTable(lines, ["arm", "median [p25–p75] across rows and passes"], Durations(campaign));
        AddHeading(lines, "Product process sampling");
        AddTable(
            lines,
            [
                "pass", "row", "run.json samplerProcesses", "analysed process", "all non-self processes",
                "absent ticks", "rejected (readError)", "samplerError records",
            ],
            Sampling(campaign));
        AddParagraph(lines, SamplingNote);
        AddHeading(lines, "Per-row effective configuration hashes (sha256, first 12 hex)");
        AddTable(lines, ["pass", "row", "file", "sha256[:12]", "bytes"], Configs(campaign));
        AddHeading(lines, "Per-run metadata");
        AddTable(
            lines,
            ["pass", "run", "label", "startedUtc", "wallSeconds", "tick Hz", "arms", "run failed", "notes"],
            Metadata(campaign));
        return string.Join('\n', lines);
    }

    private static List<IReadOnlyList<string>> Info(CampaignModel campaign)
    {
        var runs = campaign.Rows.Select(run => run.Document).ToList();
        var ticks = campaign.Rows.Select(RunClocks.TickFrequency).Where(value => value is not null).Select(value => value!.Value).ToList();
        var arms = campaign.Rows.SelectMany(run => run.Arms.SortedNames)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var info = new List<IReadOnlyList<string>>();
        AddRow(info, "row ids measured", $"{string.Join(", ", campaign.RowIds)} ({campaign.RowIds.Count} rows)");
        AddRow(info, "passes", $"{string.Join(", ", campaign.PassIds)} ({campaign.PassIds.Count} passes)");
        AddRow(info, "arms present", Join(arms));
        AddRow(info,
            "tick frequency (Hz)",
            ticks.Count > 0 ? DescriptiveStats.FmtStat(ticks, 1) : "n/a (no run.json with ticks and wallSeconds)");
        AddRow(info, "logical processors", Join(Sorted(runs, Contracts.ArmKeys.Run.LogicalProcessors)));
        AddRow(info, "OS", Join(Sorted(runs, Contracts.ArmKeys.Run.OSDescription), "; "));
        AddRow(info, "client version", Join(Sorted(runs, Contracts.ArmKeys.Run.ClientVersion)));
        AddRow(info, ".NET runtime", Join(Sorted(runs, Contracts.ArmKeys.Run.FrameworkDescription), "; "));
        AddRow(info, "plan hash(es)", Join(Sorted(runs, Contracts.ArmKeys.Run.PlanHash)));
        AddRow(info, "target", Join(Targets(runs), "; "));
        AddRow(info,
            "steady-state warmup",
            $"{VerbatimNumber.Fixed(campaign.WarmupSeconds, 1)} s of every arm excluded from the memory and CPU steady-state cells");
        AddRow(info, "flat mode", campaign.Flat ? "yes (one implicit pass named 'flat')" : "no");

        foreach (var passId in campaign.PassIds)
        {
            var order = campaign.Order.TryGetValue(passId, out var ran) ? ran : [];
            AddRow(info, $"{passId} run order (order.txt)", order.Count > 0 ? string.Join(" -> ", order) : "n/a (no order.txt; pass dirs are unordered)");
            AddRow(info, $"{passId} target ledger(s)", LedgerLine(campaign, passId));
        }

        if (campaign.Environment is { ValueKind: JsonValueKind.Object } environment)
        {
            foreach (var member in environment.EnumerateObject().OrderBy(member => member.Name, StringComparer.Ordinal))
            {
                AddRow(info, $"environment.json: {member.Name}", JsonText.Of(member.Value));
            }
        }
        else
        {
            AddRow(info, "environment.json", "n/a (not found beside the pass directories)");
        }

        return info;
    }

    private static void AddRow(List<IReadOnlyList<string>> info, string item, string value) => info.Add([item, value]);

    private static string LedgerLine(CampaignModel campaign, string passId)
    {
        var paths = campaign.LedgerPaths.TryGetValue(passId, out var found) ? found : [];
        if (paths.Count == 0)
        {
            return "n/a (not found) (0 record(s), 0 unparsable line(s); attribution: n/a)";
        }

        var view = LedgerViewsFor(campaign, passId);
        var attribution = LedgerFindings.AttributionOf(campaign, passId);
        return $"{string.Join(", ", paths)} ({view.Records.ToString(CultureInfo.InvariantCulture)} record(s), "
            + $"{view.BadLines.ToString(CultureInfo.InvariantCulture)} unparsable line(s); attribution: {attribution})";
    }

    private static LedgerPassView LedgerViewsFor(CampaignModel campaign, string passId) =>
        LedgerViewsBuilder.For(campaign).Passes[passId];

    private static List<IReadOnlyList<string>> WindowRows(CampaignModel campaign)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var (armName, path, parameter) in new[]
        {
            ("LOSS", "metrics/window", "parameters/lossWindowMs"),
            ("MIX", "metrics/classes/udp/window", "parameters/lossWindowMs"),
            ("BASE", "metrics/loss/window", "parameters/loss/lossWindowMs"),
        })
        {
            var values = Published(campaign, armName, path);
            var declared = Published(campaign, armName, parameter);
            rows.Add(
            [
                armName,
                path.Replace(JsonValue.Separator, '.'),
                values.Count > 0 ? DescriptiveStats.FmtStat(values, 1, " ms") : $"n/a (no {armName} arm with that metric)",
                declared.Count > 0 ? DescriptiveStats.FmtStat(declared, 1, " ms") : "n/a",
            ]);
        }

        var gateValues = new List<double>();
        foreach (var row in campaign.Rows)
        {
            foreach (var arm in row.Arms.All)
            {
                var value = ArmAccess.Number(row, arm.Name, "gates/windowMs").Value;
                if (value is not null and not 0.0)
                {
                    gateValues.Add(value.Value);
                }
            }
        }

        rows.Add(
        [
            "any arm (client-side gate)",
            "gates.windowMs",
            gateValues.Count > 0 ? DescriptiveStats.FmtStat(gateValues, 1, " ms") : "n/a (no non-zero client gate window)",
            "—",
        ]);
        return rows;
    }

    private static List<double> Published(CampaignModel campaign, string armName, string path)
    {
        var values = new List<double>();
        foreach (var row in campaign.Rows)
        {
            var value = ArmAccess.Number(row, armName, path).Value;
            if (value is not null)
            {
                values.Add(value.Value);
            }
        }

        return values;
    }

    private static List<IReadOnlyList<string>> Durations(CampaignModel campaign)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var armName in ArmRecords.LoadOrder)
        {
            var values = campaign.Rows
                .Select(run => RunClocks.ArmWindowSeconds(run, armName))
                .Where(value => value is not null)
                .Select(value => value!.Value)
                .ToList();
            rows.Add([armName, values.Count > 0 ? DescriptiveStats.FmtStat(values, 1, " s") : $"n/a (no {armName} arm)"]);
        }

        return rows;
    }

    private static List<IReadOnlyList<string>> Sampling(CampaignModel campaign)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var run in campaign.Rows)
        {
            var samples = RunSamples.Product(run);
            var (primary, allNames) = PrimaryProductProcess(run);
            var absent = samples.Count(sample => JsonValue.IsTrue(sample, "absent"));
            var rejected = samples.Count(sample => !RunSamples.IsReadable(sample));
            rows.Add(
            [
                run.PassId,
                run.RunId,
                ConfiguredProcesses(run),
                primary ?? "n/a (no non-self samples)",
                allNames.Count > 0 ? string.Join(", ", allNames.Select(entry => $"{entry.Name} x{entry.Count}")) : "none",
                samples.Count > 0
                    ? $"{absent.ToString(CultureInfo.InvariantCulture)} of {samples.Count.ToString(CultureInfo.InvariantCulture)}"
                    : "n/a",
                rejected.ToString(CultureInfo.InvariantCulture),
                RunSamples.SamplerErrors(run).Count.ToString(CultureInfo.InvariantCulture),
            ]);
        }

        return rows;
    }

    private static string ConfiguredProcesses(ClientRun run)
    {
        var processes = JsonValue.Array(run.Document, Contracts.ArmKeys.Run.SamplerProcesses) ?? [];
        return processes.Count > 0
            ? string.Join(", ", processes.Select(process => JsonText.Of(process)))
            : "n/a (none configured)";
    }

    private static List<IReadOnlyList<string>> Configs(CampaignModel campaign)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var run in campaign.Rows)
        {
            if (run.Configs.Count == 0)
            {
                rows.Add([run.PassId, run.RunId, "n/a", "n/a (no config* file in the row)", "n/a"]);
                continue;
            }

            foreach (var config in run.Configs)
            {
                rows.Add([
                    run.PassId,
                    run.RunId,
                    config.Name,
                    config.Digest,
                    config.Bytes.ToString(CultureInfo.InvariantCulture),
                ]);
            }
        }

        return rows;
    }

    private static List<IReadOnlyList<string>> Metadata(CampaignModel campaign)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var run in EveryRun(campaign))
        {
            rows.Add(
            [
                run.PassId,
                run.RunId,
                JsonValue.String(run.Document, Contracts.ArmKeys.Run.Label) ?? "n/a",
                JsonValue.String(run.Document, Contracts.ArmKeys.Run.StartedUtc) ?? "n/a",
                VerbatimNumber.Cell(JsonValue.Number(run.Document, Contracts.ArmKeys.Run.WallSeconds), 1),
                VerbatimNumber.Cell(RunClocks.TickFrequency(run), 1),
                run.Arms.Count.ToString(CultureInfo.InvariantCulture),
                RunFailed(run),
                run.LoadErrors.Count > 0 ? string.Join("; ", run.LoadErrors) : "—",
            ]);
        }

        return rows;
    }

    private static List<ClientRun> EveryRun(CampaignModel campaign)
    {
        var runs = new List<ClientRun>();
        foreach (var run in campaign.Rows)
        {
            runs.Add(run);
            runs.AddRange(new[] { run.DualProxied, run.DualDirect }.OfType<ClientRun>());
        }

        return runs;
    }

    private static string RunFailed(ClientRun run)
    {
        if (run.Document is null)
        {
            return "n/a (no run.json)";
        }

        return JsonValue.IsTrue(run.Document, Contracts.ArmKeys.Run.Failed) ? "yes" : "no";
    }

    private static (string? Primary, List<(string Name, int Count)> All) PrimaryProductProcess(ClientRun run)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sample in RunSamples.Product(run))
        {
            var name = JsonValue.String(sample, "process") ?? string.Empty;
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        var ranked = counts
            .Select(entry => (entry.Key, entry.Value))
            .OrderBy(entry => -entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (Name: entry.Key, Count: entry.Value))
            .ToList();
        return ranked.Count == 0 ? (null, ranked) : (ranked[0].Name, ranked);
    }

    private static List<string> Sorted(List<JsonElement?> documents, string name) =>
    [
        .. documents
            .Select(document => JsonText.Of(JsonValue.Member(document, name)))
            .Where(value => !string.Equals(value, JsonText.Of(element: null), StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    private static List<string> Targets(List<JsonElement?> documents) =>
    [
        .. documents
            .Where(document => JsonValue.Dig(document, "target/address") is not null)
            .Select(document => $"{JsonText.Of(JsonValue.Dig(document, "target/address"))} tcp/{JsonText.Of(JsonValue.Dig(document, "target/tcpPort"))} "
                + $"udp/{JsonText.Of(JsonValue.Dig(document, "target/udpPort"))} dns")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    private static string Join(List<string> values, string separator = ", ") => values.Count > 0 ? string.Join(separator, values) : "n/a";

    private static void AddHeading(List<string> lines, string title)
    {
        lines.Add($"### {title}");
        lines.Add(string.Empty);
    }

    private static void AddParagraph(List<string> lines, string text)
    {
        lines.Add(text);
        lines.Add(string.Empty);
    }

    private static void AddTable(List<string> lines, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        lines.Add(MarkdownTable.Render(headers, rows));
        lines.Add(string.Empty);
    }
}
