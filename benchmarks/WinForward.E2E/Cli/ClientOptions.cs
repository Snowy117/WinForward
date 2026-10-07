using System.Globalization;
using System.Net;

namespace WinForward.E2E.Cli;

/// <summary>
/// The client verb's argument surface, after every option has been parsed and before a plan is read.
/// </summary>
internal sealed class ClientOptions
{
    internal string TargetAddress { get; set; } = string.Empty;

    internal string? PlanPath { get; set; }

    internal string OutDirectory { get; set; } = string.Empty;

    internal string Label { get; set; } = string.Empty;

    internal List<string> SamplerProcesses { get; } = [];

    internal int TcpPort { get; private set; } = 30010;

    internal int UdpPort { get; private set; } = 30010;

    internal int DnsPort { get; private set; } = 53;

    internal int InjectCorruptEvery { get; private set; }

    internal int InjectRewriteEvery { get; private set; }

    private static readonly string[] s_knownOptions =
    [
        "--target",
        "--plan",
        "--out",
        "--label",
        "--sampler-process",
        "--tcp-port",
        "--udp-port",
        "--dns-port",
        "--inject-corrupt-every",
        "--inject-rewrite-every",
    ];

    private static readonly string[] s_stringOptions =
    [
        "--target",
        "--plan",
        "--out",
        "--label",
        "--sampler-process",
    ];

    /// <summary>
    /// Reads the verb's command line: the shared walk (the walk-level refusals are
    /// <see cref="CommandLine"/>'s) and then the requirement that no other option can stand in for:
    /// a run with no target or no output directory has nothing to do.
    /// </summary>
    internal static bool TryCreate(string[] args, out ClientOptions options, out string? error)
    {
        options = new ClientOptions();

        if (!CommandLine.TryParse(args, s_knownOptions, options, TryApply, out error))
        {
            return false;
        }

        if (options.TargetAddress.Length == 0)
        {
            error = "--target is required";
            return false;
        }

        if (!IPAddress.TryParse(options.TargetAddress, out _))
        {
            error = $"--target '{options.TargetAddress}' is not an IP address literal";
            return false;
        }

        if (options.OutDirectory.Length == 0)
        {
            error = "--out is required";
            return false;
        }

        return true;
    }

    private static bool TryApply(ClientOptions options, string name, string value, out string? error)
    {
        error = null;

        // A string option consumes the next argument when it has no inline value, so a forgotten
        // value swallows the option that follows it and the run is configured by accident. Numbers
        // cannot reach this check: a leading '-' already fails their parser.
        if (value.StartsWith('-') && Array.IndexOf(s_stringOptions, name) >= 0)
        {
            error = $"'{name}' value '{value}' starts with '-'; a value that looks like an option usually means its own is missing";
            return false;
        }

        switch (name)
        {
            case "--target":
                options.TargetAddress = value;
                return true;
            case "--plan":
                if (value.Length == 0)
                {
                    error = "--plan needs a path; omit the option to run the built-in plan";
                    return false;
                }

                options.PlanPath = value;
                return true;
            case "--out":
                options.OutDirectory = value;
                return true;
            case "--label":
                options.Label = value;
                return true;
            case "--sampler-process":
                if (value.Length == 0)
                {
                    error = "--sampler-process needs a process name; a process is matched by name and an empty one matches nothing";
                    return false;
                }

                options.SamplerProcesses.Add(value);
                return true;
            case "--tcp-port":
                return TryAssignPort(value, out error, static (target, port) => target.TcpPort = port, options);
            case "--udp-port":
                return TryAssignPort(value, out error, static (target, port) => target.UdpPort = port, options);
            case "--dns-port":
                return TryAssignPort(value, out error, static (target, port) => target.DnsPort = port, options);
            case "--inject-corrupt-every":
                return TryAssignCount(value, out error, static (target, count) => target.InjectCorruptEvery = count, options);
            case "--inject-rewrite-every":
                return TryAssignCount(value, out error, static (target, count) => target.InjectRewriteEvery = count, options);
            default:
                error = $"unknown argument '{name}'";
                return false;
        }
    }

    private static bool TryAssignPort(string value, out string? error, Action<ClientOptions, int> assign, ClientOptions options)
    {
        if (!TryPort(value, out var port, out error))
        {
            return false;
        }

        assign(options, port);
        return true;
    }

    private static bool TryAssignCount(string value, out string? error, Action<ClientOptions, int> assign, ClientOptions options)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0)
        {
            error = $"'{value}' is not a positive count";
            return false;
        }

        error = null;
        assign(options, count);
        return true;
    }

    private static bool TryPort(string value, out int port, out string? error)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535)
        {
            error = null;
            return true;
        }

        port = 0;
        error = $"'{value}' is not a port number in 1..65535";
        return false;
    }
}
