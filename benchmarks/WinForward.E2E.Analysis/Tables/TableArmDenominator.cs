using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Tables;

/// <summary>
/// What one arm's CPU should be divided by to become a per-transaction figure: the two denominators,
/// and the words §6's last two columns name them with.
/// </summary>
/// <param name="Transactions">Transactions the arm completed, or null when it has no such denominator.</param>
/// <param name="Datagrams">Datagrams the arm sent, or null when the row carries no UDP on this arm.</param>
/// <param name="TransactionLabel">How the transaction denominator is named, or null.</param>
/// <param name="DatagramLabel">How the datagram denominator is named, or null.</param>
internal sealed record ArmDenominators(
    double? Transactions,
    double? Datagrams,
    string? TransactionLabel,
    string? DatagramLabel);

/// <summary>
/// The per-arm transaction and datagram denominators §6 normalises CPU by, read from the arm's own kind.
/// </summary>
/// <remarks>
/// <para><b>The denominator is a property of the arm's kind, not of its name.</b> A latency arm counts
/// its TCP and UDP sends, a loss arm counts datagrams, a reliability arm counts connect attempts, a
/// throughput arm counts frames, a DNS arm counts queries, a MIX arm sums its four classes, and a
/// persistent arm counts requests; the words the column prints come from the same place.</para>
/// <para><b>A row that cannot carry UDP has no datagram denominator at all.</b> The column reads
/// <c>n/a (no datagram denominator)</c> rather than a fabricated zero, because dividing by a datagram
/// count the row never produced would report a cost per datagram that does not exist.</para>
/// </remarks>
internal static class TableArmDenominator
{
    /// <summary>One arm's denominators and their names, or an empty set for a kind this table does not know.</summary>
    /// <param name="row">The run the arm belongs to.</param>
    /// <param name="armName">The arm to read.</param>
    internal static ArmDenominators Of(ClientRun row, string armName)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(armName);

        var incapable = RowProfiles.UdpIncapableRows.Contains(row.RunId);
        return row.Arms.Find(armName)?.Kind switch
        {
            "latency" => Latency(row, armName, incapable),
            "loss" => Loss(row, armName),
            "reliability" => One(ArmAccess.Number(row, armName, "metrics/connectAttempts"), "connectAttempts"),
            "throughput" => One(ArmAccess.Number(row, armName, "metrics/frames"), "frames"),
            "dns" => Dns(row, armName, incapable),
            "mix" => Mix(row, armName, incapable),
            "persistent" => One(ArmAccess.Number(row, armName, "metrics/requests"), "requests"),
            _ => new ArmDenominators(Transactions: null, Datagrams: null, TransactionLabel: null, DatagramLabel: null),
        };
    }

    private static ArmDenominators Latency(ClientRun row, string armName, bool incapable)
    {
        var tcp = ArmAccess.Number(row, armName, "metrics/tcp.sent").Value;
        var udp = incapable ? null : ArmAccess.Number(row, armName, "metrics/udp.sent").Value;
        return new ArmDenominators(
            (tcp ?? 0.0) + (udp ?? 0.0),
            Datagrams: udp,
            TransactionLabel: "tcp.sent + udp.sent",
            DatagramLabel: "udp.sent");
    }

    private static ArmDenominators Loss(ClientRun row, string armName)
    {
        var sent = ArmAccess.Number(row, armName, "metrics/sent").Value;
        return new ArmDenominators(
            sent,
            Datagrams: sent,
            TransactionLabel: "sent (datagrams)",
            DatagramLabel: "sent (datagrams)");
    }

    private static ArmDenominators Dns(ClientRun row, string armName, bool incapable)
    {
        var sent = ArmAccess.Number(row, armName, "metrics/sent").Value;
        var udp = incapable ? null : ArmAccess.Number(row, armName, "metrics/udp.sent").Value;
        return new ArmDenominators(
            sent,
            Datagrams: udp,
            TransactionLabel: "sent (queries)",
            DatagramLabel: "udp.sent (queries)");
    }

    private static ArmDenominators Mix(ClientRun row, string armName, bool incapable)
    {
        var pages = ArmAccess.Number(row, armName, "metrics/classes/page/messages").Value;
        var bulk = ArmAccess.Number(row, armName, "metrics/classes/bulk/frames").Value;
        var dns = ArmAccess.Number(row, armName, "metrics/classes/dns/sent").Value;
        var udp = incapable ? null : ArmAccess.Number(row, armName, "metrics/classes/udp/sent").Value;
        return new ArmDenominators(
            (pages ?? 0.0) + (bulk ?? 0.0) + (dns ?? 0.0) + (udp ?? 0.0),
            Datagrams: udp,
            TransactionLabel: "page.messages + bulk.frames + dns.sent + udp.sent",
            DatagramLabel: "udp.sent");
    }

    private static ArmDenominators One(Measured<double?> reading, string label) =>
        new(reading.Value, Datagrams: null, TransactionLabel: label, DatagramLabel: null);
}
