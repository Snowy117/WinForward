namespace WinForward.E2E.Target;

internal sealed class TargetOptions
{
    internal string BindAddress { get; set; } = "0.0.0.0";

    internal int TcpPort { get; set; } = 30010;

    internal int UdpPort { get; set; } = 30010;

    internal int DnsPort { get; set; } = 30053;

    /// <summary>
    /// A second DNS responder port. Some measured products special-case destination port 53, so comparing
    /// DNS through the proxy across products needs a port none of them handles and the run needs both
    /// listeners at once.
    /// </summary>
    internal int? DnsAltPort { get; set; }

    /// <summary>Run or row identity the campaign driver supplies; every ledger record carries it.</summary>
    internal string Label { get; set; } = string.Empty;

    internal string LedgerPath { get; set; } = "target-ledger.jsonl";

    internal static readonly string[] s_knownOptions =
    [
        "--bind",
        "--tcp-port",
        "--udp-port",
        "--dns-port",
        "--dns-alt-port",
        "--label",
        "--ledger",
    ];
}
