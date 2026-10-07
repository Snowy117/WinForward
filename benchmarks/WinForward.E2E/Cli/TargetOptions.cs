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

    private static readonly string[] s_knownOptions =
    [
        "--bind",
        "--tcp-port",
        "--udp-port",
        "--dns-port",
        "--dns-alt-port",
        "--label",
        "--ledger",
    ];

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

            if (Array.IndexOf(s_knownOptions, name) < 0)
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

        return ValidatePorts(options, out error);
    }
#pragma warning restore RCS1239

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
        // two numbers are equal by definition, so the option names are what tell the two ports apart.
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
}
