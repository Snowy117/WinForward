using System.Diagnostics;
using System.Globalization;
using System.Net;
using WinForward.E2E.Cli;

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
        await using var ledger = new LedgerWriter(options.LedgerPath, options.Label);
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

    private static async ValueTask WriteSummariesAsync(
        LedgerWriter ledger,
        TcpTargetServer tcp,
        UdpEchoServer udp,
        DnsServer dns,
        DnsServer? dnsAlt,
        long startedTicks,
        long endedTicks)
    {
        await udp.WriteSummaryAsync(CancellationToken.None).ConfigureAwait(false);
        await dns.WriteSummaryAsync(CancellationToken.None).ConfigureAwait(false);
        if (dnsAlt is not null)
        {
            await dnsAlt.WriteSummaryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await tcp.WriteSummaryAsync(CancellationToken.None).ConfigureAwait(false);
        await ledger.WriteAsync(
            writer =>
            {
                writer.WriteString("type", "targetSummary");
                writer.WriteNumber("startedTicks", startedTicks);
                writer.WriteNumber("endedTicks", endedTicks);
                writer.WriteNumber("ledgerWriteErrors", ledger.WriteErrors);
                writer.WritePropertyName("tcp");
                writer.WriteStartObject();
                tcp.WriteTotals(writer);
                writer.WriteEndObject();
                writer.WritePropertyName("udp");
                writer.WriteStartObject();
                udp.WriteTotals(writer);
                writer.WriteEndObject();
                writer.WritePropertyName("dns");
                writer.WriteStartObject();
                dns.WriteTotals(writer);
                writer.WriteEndObject();
                if (dnsAlt is not null)
                {
                    writer.WritePropertyName("dnsAlt");
                    writer.WriteStartObject();
                    dnsAlt.WriteTotals(writer);
                    writer.WriteEndObject();
                }
            },
            CancellationToken.None).ConfigureAwait(false);
    }

#pragma warning disable RCS1239 // Every flag may consume the next argument as its value, so the body advances the index and S127 (error) forbids a for loop here.
    internal static bool TryCreate(string[] args, out TargetOptions options, out string? error)
    {
        options = new TargetOptions();
        error = null;

        var index = 0;
        while (index < args.Length)
        {
            var argument = args[index];
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            var name = separator >= 0 ? argument[..separator] : argument;
            var inlineValue = separator >= 0 ? argument[(separator + 1)..] : null;

            if (Array.IndexOf(TargetOptions.s_knownOptions, name) < 0)
            {
                error = $"unknown argument '{argument}'";
                return false;
            }

            if (inlineValue is null)
            {
                if (++index >= args.Length)
                {
                    error = $"missing value for '{name}'";
                    return false;
                }

                inlineValue = args[index];
            }

            if (!Apply(options, name, inlineValue, out error))
            {
                return false;
            }

            index++;
        }

        if (options.TcpPort is < 1 or > 65535 || options.UdpPort is < 1 or > 65535 || options.DnsPort is < 1 or > 65535)
        {
            error = "ports must be in the range 1..65535";
            return false;
        }

        if (options.DnsAltPort is { } dnsAltPort)
        {
            if (dnsAltPort is < 1 or > 65535)
            {
                error = "ports must be in the range 1..65535";
                return false;
            }

            if (dnsAltPort == options.TcpPort || dnsAltPort == options.UdpPort || dnsAltPort == options.DnsPort)
            {
                error = "the additional dns port must differ from the tcp, udp and dns ports";
                return false;
            }
        }

        return true;
    }
#pragma warning restore RCS1239

    private static bool Apply(TargetOptions options, string name, string value, out string? error)
    {
        error = null;
        switch (name)
        {
            case "--bind":
                options.BindAddress = value;
                return true;
            case "--ledger":
                options.LedgerPath = value;
                return true;
            case "--tcp-port":
                if (!TryPort(value, out var tcpPort, out error))
                {
                    return false;
                }

                options.TcpPort = tcpPort;
                return true;
            case "--udp-port":
                if (!TryPort(value, out var udpPort, out error))
                {
                    return false;
                }

                options.UdpPort = udpPort;
                return true;
            case "--dns-port":
                if (!TryPort(value, out var dnsPort, out error))
                {
                    return false;
                }

                options.DnsPort = dnsPort;
                return true;
            case "--dns-alt-port":
                if (!TryPort(value, out var dnsAltPort, out error))
                {
                    return false;
                }

                options.DnsAltPort = dnsAltPort;
                return true;
            case "--label":
                options.Label = value;
                return true;
            default:
                error = $"unknown argument '{name}'";
                return false;
        }
    }

    private static bool TryPort(string value, out int port, out string? error)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port))
        {
            error = null;
            return true;
        }

        port = 0;
        error = $"'{value}' is not a port number";
        return false;
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
