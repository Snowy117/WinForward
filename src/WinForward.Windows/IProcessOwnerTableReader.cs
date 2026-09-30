using System.Net;
using System.Runtime.InteropServices;
using WinForward.Core;

namespace WinForward.Windows;

/// <summary>
/// Which system-wide connection table a read asks for: one kind per (protocol, address family)
/// pair, because the native table class and the address family are both part of the request.
/// </summary>
internal enum OwnerTableKind
{
    Tcp4,
    Tcp6,
    Udp4,
    Udp6,
}

/// <summary>One parsed UDP owner-table row: the local address, the local port and the owning PID.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct UdpOwnerRow(IPAddress Address, ushort Port, uint ProcessId);

/// <summary>One parsed TCP owner-table row: the full four-tuple and the owning PID.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct TcpOwnerRow(Endpoint Local, Endpoint Remote, uint ProcessId);

/// <summary>
/// The injectable owner-table seam. Production reads through <see cref="IPHelperOwnerTableReader"/>
/// (the single <c>iphlpapi</c> boundary); tests and the benchmarks script rows through a fake, which
/// is the only way the epoch-coalescing logic is exercisable on a host without the native tables.
/// A read is cold-path work: it runs on a setup worker, never on the capture pump.
/// </summary>
internal interface IProcessOwnerTableReader
{
    /// <summary>
    /// Reads and parses one system-wide owner table, or returns
    /// <see cref="OwnerTable.Unavailable"/> when the platform has no such table. Any other read
    /// failure throws, and the attribution caller's catch set turns it into "no owner".
    /// </summary>
    OwnerTable Read(OwnerTableKind kind);
}

/// <summary>
/// An immutable, parsed snapshot of one system-wide owner table, plus the two attribution
/// predicates. A snapshot hit and a fresh scan must produce the same answer for the same rows:
/// that equality is the precondition for serving any cached answer at all.
/// <para>
/// The UDP predicate matches the <em>local port</em> against a wildcard-or-exact address and
/// accepts the PID only when exactly one distinct PID matches; the TCP predicate requires all four
/// tuple fields. That asymmetry is why only TCP rows may be served from a snapshot older than the
/// caller's request (see <see cref="ProcessOwnerTableCache"/>).
/// </para>
/// </summary>
internal sealed class OwnerTable
{
    private readonly UdpOwnerRow[] _udpRows;
    private readonly TcpOwnerRow[] _tcpRows;
    private readonly IPAddress _wildcard;

    public OwnerTable(OwnerTableKind kind, UdpOwnerRow[] udpRows, TcpOwnerRow[] tcpRows)
    {
        ArgumentNullException.ThrowIfNull(udpRows);
        ArgumentNullException.ThrowIfNull(tcpRows);
        Kind = kind;
        _udpRows = udpRows;
        _tcpRows = tcpRows;
        _wildcard = FamilyOf(kind) == AddressFamilyKind.IPv4 ? IPAddress.Any : IPAddress.IPv6Any;
        IsAvailable = true;
    }

    private OwnerTable()
    {
        Kind = OwnerTableKind.Tcp4;
        _udpRows = [];
        _tcpRows = [];
        _wildcard = IPAddress.Any;
        IsAvailable = false;
    }

    /// <summary>
    /// The "this platform has no owner tables" answer: no rows, so no lookup can match and no
    /// caller may cache it as a positive result.
    /// </summary>
    public static OwnerTable Unavailable { get; } = new();

    public OwnerTableKind Kind { get; }

    /// <summary>False for <see cref="Unavailable"/>: no rows were read, so no answer may be cached.</summary>
    public bool IsAvailable { get; }

    /// <summary>The address family this table's rows belong to.</summary>
    private AddressFamilyKind Family => FamilyOf(Kind);

    /// <summary>The table kind a flow key reads, or null for a protocol with no owner table.</summary>
    public static OwnerTableKind? KindOf(FlowKey key) => (key.Protocol, key.AddressFamily) switch
    {
        (TransportProtocol.Tcp, AddressFamilyKind.IPv4) => OwnerTableKind.Tcp4,
        (TransportProtocol.Tcp, AddressFamilyKind.IPv6) => OwnerTableKind.Tcp6,
        (TransportProtocol.Udp, AddressFamilyKind.IPv4) => OwnerTableKind.Udp4,
        (TransportProtocol.Udp, AddressFamilyKind.IPv6) => OwnerTableKind.Udp6,
        _ => null,
    };

    private static AddressFamilyKind FamilyOf(OwnerTableKind kind) =>
        kind is OwnerTableKind.Tcp4 or OwnerTableKind.Udp4 ? AddressFamilyKind.IPv4 : AddressFamilyKind.IPv6;

    /// <summary>The owning PID for a flow key, or null when the table holds no unique owner for it.</summary>
    public uint? Lookup(FlowKey key) => key.Protocol switch
    {
        TransportProtocol.Udp => FindUdpOwner(key.Local),
        TransportProtocol.Tcp => FindTcpOwner(key.Local, key.Remote),
        _ => null,
    };

    /// <summary>
    /// The UDP rule: rows whose local port matches and whose address is the family wildcard or the
    /// queried address, accepted only when exactly one distinct PID matches. A mismatched address
    /// family is refused (fail-closed) instead of being compared against the wrong wildcard.
    /// </summary>
    private uint? FindUdpOwner(Endpoint local)
    {
        if (!IsAvailable || local.AddressFamily != Family) return null;
        var localAddress = local.Address;
        var found = false;
        uint single = 0;
        foreach (var row in _udpRows)
        {
            if (row.Port != local.Port) continue;
            if (!row.Address.Equals(_wildcard) && !IPAddressValue.From(row.Address).Equals(localAddress)) continue;
            if (found)
            {
                if (row.ProcessId == single) continue;
                return null;
            }

            single = row.ProcessId;
            found = true;
        }

        return found ? single : null;
    }

    /// <summary>
    /// The TCP rule: rows matching all four tuple fields, accepted only when exactly one distinct
    /// PID matches. The full four-tuple match is what bounds the staleness of a cached TCP answer:
    /// a row can only survive into a later request if the same connection still exists.
    /// </summary>
    private uint? FindTcpOwner(Endpoint local, Endpoint remote)
    {
        if (!IsAvailable || local.AddressFamily != Family || remote.AddressFamily != Family) return null;
        var found = false;
        uint single = 0;
        foreach (var row in _tcpRows)
        {
            if (row.Local.Port != local.Port || row.Remote.Port != remote.Port) continue;
            if (!row.Local.Address.Equals(local.Address) || !row.Remote.Address.Equals(remote.Address)) continue;
            if (found)
            {
                if (row.ProcessId == single) continue;
                return null;
            }

            single = row.ProcessId;
            found = true;
        }

        return found ? single : null;
    }
}

/// <summary>
/// The reader for a host with no owner tables: every kind reports
/// <see cref="OwnerTable.Unavailable"/>, so the managed cache logic (and every fact built on it)
/// runs on any host while such a host keeps the pre-existing "no attribution" result.
/// </summary>
internal sealed class UnavailableOwnerTableReader : IProcessOwnerTableReader
{
    public static UnavailableOwnerTableReader Instance { get; } = new();

    private UnavailableOwnerTableReader()
    {
    }

    public OwnerTable Read(OwnerTableKind kind) => OwnerTable.Unavailable;
}
