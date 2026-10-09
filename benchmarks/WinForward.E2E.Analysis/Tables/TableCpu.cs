using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Stats;
using WinForward.E2E.Contracts;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// §6, "CPU detail": one line per (row, arm), the proxy and generator CPU each as a share of one
/// logical processor, the headroom, the two per-work denominators, and the shape of the reading.
/// </summary>
/// <remarks>
/// <para><b>Proxy and generator are different processes.</b> The proxy cells are the product process the
/// sampler matched; the generator cells are the sampler's own records, read through the same per-identity
/// accumulation but from the <c>self</c> series, so the cost of generating the load is never attributed
/// to the product.</para>
/// <para><b>The scope disclosure is part of the table.</b> Every number here is the sampled process's own
/// <c>Process.TotalProcessorTime</c>, so kernel-mode work the product causes outside its own threads is
/// measured nowhere; the paragraph below the table says so in full rather than leaving the reader to
/// assume the column is a machine cost.</para>
/// <para><b>A row with no product process still gets a line.</b> The two control blocks load no product,
/// so their lines say so once and repeat <c>n/a</c> for every column the reason would not fit, rather
/// than being dropped from a table whose subject is every measured arm.</para>
/// </remarks>
internal static class TableCpu
{
    private const string Caption = "Per (row, arm). `proxy %vCPU` and `generator %vCPU` are computed **per process identity** `(pid, startUtc)`: "
                                   + "each identity contributes the delta of its own cumulative `cpuSeconds` between its first and last readable "
                                   + "sample, the deltas are summed, and the sum is divided by the wall-clock span of the arm's sample stream, so a "
                                   + "process that restarted mid-run cannot corrupt the total the way a first/last difference over a process *name* "
                                   + "does. Samples carrying `readError` are rejected (their counters are `null`, not `0`) and counted in the last "
                                   + "column. `tickFrequency` is derived per run from `(endedTicks - startedTicks) / wallSeconds`; the counters are "
                                   + "cumulative `Process.TotalProcessorTime` (100 % = one fully busy logical processor). `%machine` divides by the "
                                   + "logical processor count. `headroom` is `100 x (P x 100 - generator %vCPU - proxy %vCPU) / (P x 100)` with P "
                                   + "logical processors; machine-wide CPU is not sampled, so headroom is against logical-processor capacity, not a "
                                   + "measured machine total. `CPU ms / 1000 tx` and `CPU ms / 1000 datagrams` normalise the arm's proxy CPU by the "
                                   + "denominators named in the last two columns. All cells are `median [p25–p75] across passes (n=K)`.";

    private const string Scope = "**CPU scope: user-mode only — no kernel-mode work outside the process.** Every cell above is the sampled "
                                 + "process's own `Process.TotalProcessorTime`: the time the operating system charges to *that process's* "
                                 + "threads, its user time plus the privileged time those threads spend in system calls. Work the product causes "
                                 + "in kernel mode outside those threads is measured nowhere in this table: interrupt, DPC and ISR time in a "
                                 + "kernel data path, packets a driver serves on behalf of other processes, and machine-wide CPU are all outside "
                                 + "the number. A kernel-heavy product can therefore show a low cell here while still costing the machine real "
                                 + "CPU; compare the columns as equally-scoped process CPU, never as a product's total cost.";

    private const string DenominatorNote = "**Per-arm transaction and datagram denominators.** `LAT`, `LATLOAD`: `metrics.tcp.sent + metrics.udp.sent`; "
                                           + "`DNS`: `metrics.sent`; `LOSS`: `metrics.sent`; `REL`: `metrics.connectAttempts`; `PERSIST`: "
                                           + "`metrics.requests`; `THRU`: `metrics.frames`; `MIX`: `classes.page.messages + classes.bulk.frames + "
                                           + "classes.dns.sent + classes.udp.sent`. Datagram denominators: `udp.sent` (LAT/LATLOAD), `sent` (LOSS), "
                                           + "`udp.sent` (DNS), `classes.udp.sent` (MIX). A row that does not carry UDP gets no datagram denominator at all "
                                           + "— the column reads `n/a (no datagram denominator)` rather than a fabricated zero — and `IDLE` and `BASE` have "
                                           + "no natural transaction denominator.";

    private const string NoProductProcess = "no product process was sampled";

    private const string NoProductSamples = "no product samples";

    private const string NoDenominator = "n/a (no denominator)";

    private const string NoDatagramDenominator = "n/a (no datagram denominator)";

    private const string NoMachineCount = "n/a (no logicalProcessors)";

    private const string Unavailable = "n/a";

    private static readonly string[] s_headers =
    [
        "row",
        "arm",
        "proxy %vCPU",
        "proxy %machine",
        "generator %vCPU",
        "headroom %",
        "CPU ms / 1000 tx",
        "CPU ms / 1000 datagrams",
        "identities used/total",
        "restarts",
        "rejected samples",
        "tx denominator",
        "datagram denominator",
    ];

    /// <summary>The section's body, without its heading and ending in a newline.</summary>
    /// <param name="campaign">The loaded campaign every sample is read from.</param>
    /// <returns>The body's text.</returns>
    internal static string RenderBody(CampaignModel campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var rows = campaign.RowIds
            .SelectMany(rowId => ArmRecords.LoadOrder
                .Where(armName => campaign.RunsOf(rowId).Any(run => run.Arms.Contains(armName)))
                .Select(armName => Line(campaign, rowId, armName)))
            .ToList<IReadOnlyList<string>>();

        var lines = new List<string>
        {
            Caption,
            string.Empty,
            MarkdownTable.Render(s_headers, rows),
            string.Empty,
            Scope,
            string.Empty,
            DenominatorNote,
            string.Empty,
        };

        return string.Join('\n', lines);
    }

    /// <summary>One (row, arm) pair's line: the cells the arm's samples produced, or why it produced none.</summary>
    private static List<string> Line(CampaignModel campaign, string rowId, string armName)
    {
        var reading = Read(campaign, rowId, armName);
        if (reading.Proxy.Count == 0)
        {
            var reason = reading.Reasons.Count > 0 ? reading.Reasons.Min! : NoProductSamples;
            return [rowId, armName, $"n/a ({reason})", .. Absent(s_headers.Length - 3)];
        }

        return
        [
            rowId,
            armName,
            DescriptiveStats.FmtStat(reading.Proxy, 2, " %"),
            reading.Machine.Count > 0 ? DescriptiveStats.FmtStat(reading.Machine, 3, " %") : NoMachineCount,
            reading.Generator.Count > 0 ? DescriptiveStats.FmtStat(reading.Generator, 2, " %") : Unavailable,
            reading.Headroom.Count > 0 ? DescriptiveStats.FmtStat(reading.Headroom, 2, " %") : Unavailable,
            reading.PerTransaction.Count > 0
                ? DescriptiveStats.FmtStat(reading.PerTransaction, 4, " ms")
                : NoDenominator,
            reading.PerDatagram.Count > 0
                ? DescriptiveStats.FmtStat(reading.PerDatagram, 4, " ms")
                : NoDatagramDenominator,
            Join(reading.Identities, Unavailable),
            Join(reading.Restarts, "0"),
            Join(reading.Rejected, "0"),
            Join(reading.TransactionLabels, Unavailable),
            Join(reading.DatagramLabels, Unavailable),
        ];
    }

    /// <summary>Every pass's contribution to one (row, arm) pair's line.</summary>
    private static CpuReading Read(CampaignModel campaign, string rowId, string armName)
    {
        var reading = new CpuReading();
        foreach (var passId in campaign.PassIds)
        {
            if (campaign.InPass(passId, rowId) is { } row)
            {
                Pass(reading, row, armName);
            }
        }

        return reading;
    }

    /// <summary>One pass of one arm: the proxy and generator readings, the headroom and the denominators.</summary>
    private static void Pass(CpuReading reading, ClientRun row, string armName)
    {
        if (RunSamples.PrimaryProductProcess(row) is not { } primary)
        {
            reading.Reasons.Add(NoProductProcess);
            return;
        }

        Note(reading.Rejected, RejectedSamples(row, armName, primary));
        var frequency = RunClocks.TickFrequency(row);
        var product = RunSamples.PresentProductSamples(row, armName, primary);
        var (proxy, why, diagnostics) = CpuDetail.Compute(product, frequency);
        if (diagnostics.Identities > 0)
        {
            reading.Identities.Add(
                string.Create(CultureInfo.InvariantCulture, $"{diagnostics.IdentitiesUsed}/{diagnostics.Identities}"));
        }

        Note(reading.Restarts, diagnostics.Restarts);
        if (proxy is null)
        {
            reading.Reasons.Add(why ?? "unavailable");
        }
        else
        {
            reading.Proxy.Add(proxy.Value);
            if (Processors(row) is { } count)
            {
                reading.Machine.Add(proxy.Value / count);
            }
        }

        var (generator, generatorWhy, _) = CpuDetail.Compute(
            RunSamples.Arm(row, armName, selfOnly: true),
            frequency,
            CpuDetail.GeneratorField);
        if (generator is null)
        {
            reading.Reasons.Add(generatorWhy ?? "unavailable");
        }
        else
        {
            reading.Generator.Add(generator.Value);
        }

        if (proxy is { } proxyValue && generator is { } generatorValue && Processors(row) is { } capacity)
        {
            var full = 100.0 * capacity;
            reading.Headroom.Add(100.0 * (full - generatorValue - proxyValue) / full);
        }

        AddDenominators(reading, row, armName, product);
    }

    /// <summary>How many of the arm's samples of the primary process could not be read at all.</summary>
    private static int RejectedSamples(ClientRun row, string armName, string primary) =>
        RunSamples.Arm(row, armName).Count(sample => RunSamples.IsProcess(sample, primary) && !RunSamples.IsReadable(sample));

    /// <summary>The run's logical processor count, or null when it published none or published zero.</summary>
    private static double? Processors(ClientRun row) =>
        JsonValue.Number(row.Document, ArmKeys.Run.LogicalProcessors) is { } count && count.CompareTo(0.0) != 0
            ? count
            : null;

    /// <summary>The two per-work figures, or nothing when the arm's CPU or its denominator is missing.</summary>
    private static void AddDenominators(CpuReading reading, ClientRun row, string armName, List<JsonElement> product)
    {
        var cpuSeconds = product.Count > 0 ? CpuDetail.IdentityCpuSeconds(product) : null;
        if (cpuSeconds is not { } seconds)
        {
            return;
        }

        var denominators = TableArmDenominator.Of(row, armName);
        if (denominators.Transactions is { } transactions && transactions.CompareTo(0.0) != 0)
        {
            reading.PerTransaction.Add(1e6 * seconds / transactions);
            reading.TransactionLabels.Add(denominators.TransactionLabel!);
        }

        if (denominators.Datagrams is { } datagrams && datagrams.CompareTo(0.0) != 0)
        {
            reading.PerDatagram.Add(1e6 * seconds / datagrams);
            reading.DatagramLabels.Add(denominators.DatagramLabel!);
        }
    }

    /// <summary>Records one pass's count as a note, which is the reference's own zero-is-not-a-note test.</summary>
    private static void Note(SortedSet<string> notes, int count)
    {
        if (count > 0)
        {
            notes.Add(count.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The notes of one set joined for a cell, or the cell's own fallback when it is empty.</summary>
    private static string Join(SortedSet<string> notes, string fallback) =>
        notes.Count > 0 ? string.Join(", ", notes) : fallback;

    /// <summary>One value repeated for a line that has nothing to print but its reason.</summary>
    private static string[] Absent(int count)
    {
        var cells = new string[count];
        Array.Fill(cells, Unavailable);
        return cells;
    }

    /// <summary>Everything one (row, arm) pair's passes contributed before it is rendered.</summary>
    private sealed class CpuReading
    {
        internal List<double> Proxy { get; } = [];

        internal List<double> Machine { get; } = [];

        internal List<double> Generator { get; } = [];

        internal List<double> Headroom { get; } = [];

        internal List<double> PerTransaction { get; } = [];

        internal List<double> PerDatagram { get; } = [];

        internal SortedSet<string> TransactionLabels { get; } = new(StringComparer.Ordinal);

        internal SortedSet<string> DatagramLabels { get; } = new(StringComparer.Ordinal);

        internal SortedSet<string> Identities { get; } = new(StringComparer.Ordinal);

        internal SortedSet<string> Restarts { get; } = new(StringComparer.Ordinal);

        internal SortedSet<string> Rejected { get; } = new(StringComparer.Ordinal);

        internal SortedSet<string> Reasons { get; } = new(StringComparer.Ordinal);
    }
}
