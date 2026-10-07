using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using WinForward.E2E.Cli;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Target;

internal static class TargetRunner
{
    internal static async Task<int> RunAsync(TargetOptions options, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(options.BindAddress, out var bindAddress))
        {
            await Console.Error.WriteLineAsync($"e2e target: '{options.BindAddress}' is not a valid IP address.").ConfigureAwait(false);
            return ExitCodes.UsageError;
        }

        var workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        await using var ledger = new JsonlSink(options.LedgerPath, JsonlPolicy.SwallowAndCount, WriteLedgerEnvelope(options.Label));
        await using var tcp = new TcpTargetServer(new IPEndPoint(bindAddress, options.TcpPort), ledger);
        await using var udp = new UdpEchoServer(new IPEndPoint(bindAddress, options.UdpPort), ledger, workers);
        await using var dns = new DnsServer(new IPEndPoint(bindAddress, options.DnsPort), ledger, workers);
        await using var dnsAlt = options.DnsAltPort is { } dnsAltPort
            ? new DnsServer(new IPEndPoint(bindAddress, dnsAltPort), ledger, workers)
            : null;

        var startedTicks = Stopwatch.GetTimestamp();
        await AnnounceAsync(options, bindAddress, dnsAlt is not null).ConfigureAwait(false);

        await Task.WhenAll(
            tcp.RunAsync(cancellationToken),
            udp.RunAsync(cancellationToken),
            dns.RunAsync(cancellationToken),
            dnsAlt?.RunAsync(cancellationToken) ?? Task.CompletedTask).ConfigureAwait(false);

        var endedTicks = Stopwatch.GetTimestamp();
        await WriteSummariesAsync(ledger, tcp, udp, dns, dnsAlt, startedTicks, endedTicks).ConfigureAwait(false);

        Console.WriteLine("e2e target stopped.");
        return ExitCodes.Success;
    }

    /// <summary>
    /// Every ledger record starts with the absolute time and the label the target was started with:
    /// the target's own stopwatch cannot be aligned with the client's clock without the first, and a
    /// row cannot be attributed to an arm without the second.
    /// </summary>
    internal static Action<Utf8JsonWriter> WriteLedgerEnvelope(string label) =>
        writer =>
        {
            writer.WriteString(ArmKeys.Ledger.Envelope.Utc, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString(ArmKeys.Ledger.Envelope.Label, label);
        };

    private static async ValueTask AnnounceAsync(TargetOptions options, IPAddress bindAddress, bool hasDnsAlt)
    {
        await Console.Out.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"e2e target listening on tcp {bindAddress}:{options.TcpPort}, udp {bindAddress}:{options.UdpPort}, dns {bindAddress}:{options.DnsPort} (label '{options.Label}', ledger {Path.GetFullPath(options.LedgerPath)})")).ConfigureAwait(false);
        if (hasDnsAlt)
        {
            await Console.Out.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"e2e target additional dns listener on {bindAddress}:{options.DnsAltPort}")).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the run's four summaries in the order a reader expects them, the last of them naming the
    /// ledger's own failure count. Internal rather than private because every record of the family is
    /// published through this composition: the shape test drives it whole instead of rebuilding the
    /// record, which is the only way the conditional <c>dnsAlt</c> container is covered downstream.
    /// </summary>
    internal static async ValueTask WriteSummariesAsync(
        JsonlSink ledger,
        TcpTargetServer tcp,
        UdpEchoServer udp,
        DnsServer dns,
        DnsServer? dnsAlt,
        long startedTicks,
        long endedTicks)
    {
        // Every summary, including the targetSummary that names the failure count, is written
        // through the guard: the ledger's own policy already swallows an I/O failure, and this is
        // the second line of defence that keeps one failed summary from skipping the rest and keeps
        // any of them from escaping TargetRunner (D14.7 item 2).
        await WriteSummaryAsync(ledger, "udpSummary", () => udp.WriteSummaryAsync(CancellationToken.None)).ConfigureAwait(false);
        await WriteSummaryAsync(ledger, "dnsSummary", () => dns.WriteSummaryAsync(CancellationToken.None)).ConfigureAwait(false);
        if (dnsAlt is not null)
        {
            await WriteSummaryAsync(ledger, "dnsSummary", () => dnsAlt.WriteSummaryAsync(CancellationToken.None)).ConfigureAwait(false);
        }

        await WriteSummaryAsync(ledger, "tcpSummary", () => tcp.WriteSummaryAsync(CancellationToken.None)).ConfigureAwait(false);
        await WriteSummaryAsync(ledger, "targetSummary", () => ledger.WriteAsync(
            writer =>
            {
                writer.WriteString(ArmKeys.Common.Record.Type, "targetSummary");
                writer.WriteNumber(ArmKeys.Ledger.TargetSummary.StartedTicks, startedTicks);
                writer.WriteNumber(ArmKeys.Ledger.TargetSummary.EndedTicks, endedTicks);
                writer.WriteNumber(ArmKeys.Ledger.TargetSummary.LedgerWriteErrors, ledger.WriteErrors);
                writer.WritePropertyName(ArmKeys.Ledger.TargetSummary.Tcp);
                writer.WriteStartObject();
                tcp.WriteTotals(writer, TcpTotalsKeys.Target);
                writer.WriteEndObject();
                writer.WritePropertyName(ArmKeys.Ledger.TargetSummary.Udp);
                writer.WriteStartObject();
                udp.WriteTotals(writer);
                writer.WriteEndObject();
                writer.WritePropertyName(ArmKeys.Ledger.TargetSummary.Dns);
                writer.WriteStartObject();
                dns.WriteTotals(writer, DnsTotalsKeys.Target);
                writer.WriteEndObject();
                if (dnsAlt is not null)
                {
                    writer.WritePropertyName(ArmKeys.Ledger.TargetSummary.DnsAlt);
                    writer.WriteStartObject();
                    dnsAlt.WriteTotals(writer, DnsTotalsKeys.Target);
                    writer.WriteEndObject();
                }
            },
            CancellationToken.None)).ConfigureAwait(false);
    }

    private static async ValueTask WriteSummaryAsync(JsonlSink ledger, string name, Func<ValueTask> write)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await TargetLog.ReportAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"e2e target: the {name} record could not be written ({ledger.WriteErrors} ledger write error(s) so far): {exception.GetType().Name}: {exception.Message}")).ConfigureAwait(false);
        }
    }

    internal static void PrintHelp()
    {
        Console.WriteLine("""
            Usage: WinForward.E2E target [options]

              --bind <ip>          Address to bind (default 0.0.0.0)
              --tcp-port <n>       TCP echo/command listener port (default 30010)
              --udp-port <n>       UDP echo listener port (default 30010)
              --dns-port <n>       DNS responder port, UDP and TCP (default 30053)
              --dns-alt-port <n>   Second DNS responder port, UDP and TCP (default: none)
              --label <name>       Run or row identity copied into every ledger record (default empty)
              --ledger <path>      JSONL ledger output path (default target-ledger.jsonl)

            Runs until Ctrl+C or SIGTERM. Exits 0 on a clean shutdown, 2 on a usage error.
            """);
    }
}
