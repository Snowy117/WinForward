using System.Globalization;

namespace WinForward.E2E.Cli;

internal sealed class TargetOptions
{
    internal string BindAddress { get; private set; } = "0.0.0.0";

    internal int TcpPort { get; private set; } = 30010;

    internal int UdpPort { get; private set; } = 30010;

    internal int DnsPort { get; private set; } = 30053;

    /// <summary>
    /// A second DNS responder port. Some measured products special-case destination port 53, so comparing
    /// DNS through the proxy across products needs a port none of them handles and the run needs both
    /// listeners at once.
    /// </summary>
    internal int? DnsAltPort { get; private set; }

    /// <summary>Run or row identity the campaign driver supplies; every ledger record carries it.</summary>
    internal string Label { get; private set; } = string.Empty;

    internal string LedgerPath { get; private set; } = "target-ledger.jsonl";

    /// <summary>
    /// The deterministic delay every measured response is held for. It is the sensitivity control for
    /// the latency instrument: a known degradation that a green run must detect. Zero keeps the
    /// direct response path and allocates nothing.
    /// </summary>
    internal int InjectDelayMs { get; private set; }

    /// <summary>
    /// The receive loops every datagram listener starts: the UDP echo listener's and each DNS
    /// responder's. It decides how much of the run's datagram service is concurrent, so the value a run
    /// used is published in the ledger rather than left to the formula.
    /// </summary>
    internal int UdpReceivers { get; private set; } = DefaultUdpReceivers;

    /// <summary>
    /// The count a target started without <c>--udp-receivers</c> runs: half the processors, clamped to
    /// 2..8. The formula is a guess about how much receive concurrency a host wants, so it is the
    /// option's default and not a floor: a run that wants another value declares it.
    /// </summary>
    private static int DefaultUdpReceivers => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>The largest receive-loop count the option accepts.</summary>
    /// <remarks>
    /// A bound rather than none, because a count far above the host's core count only multiplies
    /// threads blocked on the same socket, and a typo should be refused rather than run.
    /// </remarks>
    internal const int MaxUdpReceivers = 64;

    /// <summary>The largest injection delay the option accepts, in milliseconds.</summary>
    /// <remarks>
    /// A minute is far past any calibration run's injected tail, so a value above it is a typo that
    /// would otherwise hold a campaign target open for the whole measurement.
    /// </remarks>
    internal const int MaxInjectDelayMs = 60_000;

    private static readonly string[] s_knownOptions =
    [
        "--bind",
        "--tcp-port",
        "--udp-port",
        "--udp-receivers",
        "--inject-delay-ms",
        "--dns-port",
        "--dns-alt-port",
        "--label",
        "--ledger",
    ];

    /// <summary>
    /// Reads the verb's command line with the shared walk, then applies the one check that needs the
    /// whole line: no port can be compared with another before every option has been read.
    /// </summary>
    internal static bool TryCreate(string[] args, out TargetOptions options, out string? error)
    {
        options = new TargetOptions();

        // The `&&` is deliberate: on the rejected-option path the walk leaves `error` set, and no
        // port is compared with another.
        return CommandLine.TryParse(args, s_knownOptions, options, Apply, out error)
            && ValidatePorts(options, out error);
    }

    /// <summary>
    /// The port checks, which run once every option has been read: a collision can only be seen after
    /// the whole command line is in, and a port out of range has to be refused before any of them is
    /// compared.
    /// </summary>
    private static bool ValidatePorts(TargetOptions options, out string? error)
    {
        error = null;

        if (options.TcpPort is < 1 or > 65535 || options.UdpPort is < 1 or > 65535 || options.DnsPort is < 1 or > 65535)
        {
            error = "ports must be in the range 1..65535";
            return false;
        }

        // The dns responder binds its port on tcp and udp at once, so a colliding dns port would put
        // two listeners of the same protocol on one port. Both options are named in the message: the
        // numbers are equal by definition, so the option names are what tell them apart.
        if (options.DnsPort == options.TcpPort || options.DnsPort == options.UdpPort)
        {
            var collided = options.DnsPort == options.TcpPort ? "tcp" : "udp";
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"the dns port must differ from the tcp and udp ports: --dns-port {options.DnsPort} collides with --{collided}-port {options.DnsPort}");
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
            case "--label":
                options.Label = value;
                return true;
            default:
                return TryApplyNumeric(options, name, value, out error);
        }
    }

    /// <summary>
    /// The options whose value is a number. Split from <see cref="Apply"/> so the command surface and
    /// the numeric assignments can each be read in one screen; the unknown-option answer still comes
    /// from here, because every non-string option reaches this switch.
    /// </summary>
    private static bool TryApplyNumeric(TargetOptions options, string name, string value, out string? error)
    {
        error = null;
        switch (name)
        {
            case "--tcp-port":
                return TryAssignPort(value, options, static (target, port) => target.TcpPort = port, out error);
            case "--udp-port":
                return TryAssignPort(value, options, static (target, port) => target.UdpPort = port, out error);
            case "--dns-port":
                return TryAssignPort(value, options, static (target, port) => target.DnsPort = port, out error);
            case "--dns-alt-port":
                return TryAssignPort(value, options, static (target, port) => target.DnsAltPort = port, out error);
            case "--udp-receivers":
                if (!TryReceivers(value, out var receivers, out error))
                {
                    return false;
                }

                options.UdpReceivers = receivers;
                return true;
            case "--inject-delay-ms":
                if (!TryInjectDelay(value, out var injectDelayMs, out error))
                {
                    return false;
                }

                options.InjectDelayMs = injectDelayMs;
                return true;
            default:
                error = $"unknown argument '{name}'";
                return false;
        }
    }

    private static bool TryAssignPort(string value, TargetOptions options, Action<TargetOptions, int> assign, out string? error)
    {
        if (!TryPort(value, out var port, out error))
        {
            return false;
        }

        assign(options, port);
        return true;
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

    /// <summary>
    /// The receive-loop count, in the two refusals the option has: a value that is not a decimal number
    /// at all -- a sign or a fraction included -- and one outside <see cref="MaxUdpReceivers"/>. A count
    /// of zero is refused by the same range check as a count that is too large, because either leaves a
    /// listener that serves nothing.
    /// </summary>
    private static bool TryReceivers(string value, out int receivers, out string? error)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out receivers))
        {
            error = $"'{value}' is not a receive-loop count";
            return false;
        }

        if (receivers is < 1 or > MaxUdpReceivers)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"the udp receive-loop count must be in the range 1..{MaxUdpReceivers}");
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// The deterministic response delay. A value that is not a non-negative decimal number is refused
    /// by its own message; a value past <see cref="MaxInjectDelayMs"/> is refused by the range message,
    /// the same split the receive-loop count uses. Zero is accepted because it is the documented no-op
    /// default.
    /// </summary>
    private static bool TryInjectDelay(string value, out int delay, out string? error)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out delay))
        {
            error = $"'{value}' is not a delay in milliseconds";
            return false;
        }

        if (delay > MaxInjectDelayMs)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"the injection delay must be in the range 0..{MaxInjectDelayMs}");
            return false;
        }

        error = null;
        return true;
    }
}
