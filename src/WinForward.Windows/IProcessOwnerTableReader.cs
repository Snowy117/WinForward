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
internal readonly record struct UdpOwnerRow(IPAddressValue Address, ushort Port, uint ProcessId);

/// <summary>One parsed TCP owner-table row: the full four-tuple and the owning PID.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct TcpOwnerRow(Endpoint Local, Endpoint Remote, uint ProcessId);

/// <summary>
/// The injectable owner-table seam. Production reads through <see cref="IPHelperOwnerTableReader"/>
/// (the single <c>iphlpapi</c> boundary); tests and the benchmarks script rows through a fake, which
/// is the only way the epoch-coalescing logic is exercisable on a host without the native tables.
/// A read runs on a setup worker, never on the capture pump.
/// <para>
/// The returned table is a <em>slot</em>, not a value: an implementation may return the same
/// instance on every call and refill it in place, which is what lets a read cost no managed
/// allocation once the slot's row storage has grown to the table's size. A caller therefore owns
/// the returned table only until the next <c>Read</c> of the same kind, and must search it under
/// the serialization that also guards that refill (<see cref="ProcessOwnerTableCache"/>'s per-kind
/// gate).
/// </para>
/// </summary>
internal interface IProcessOwnerTableReader
{
    /// <summary>
    /// Reads and parses one system-wide owner table, or returns an unavailable table when the
    /// platform has no such table. Any other read failure throws, and the attribution caller's
    /// catch set turns it into "no owner".
    /// </summary>
    OwnerTable Read(OwnerTableKind kind);
}

/// <summary>
/// A reusable snapshot slot for one system-wide owner table, plus the two attribution predicates.
/// The owner of the slot fills it under <see cref="BeginTcpFill"/>/<see cref="CompleteFill"/>; every
/// other reader searches it under the same serialization. A snapshot hit and a fresh scan must
/// produce the same answer for the same rows: that equality is the precondition for serving any
/// cached answer at all.
/// <para>
/// The UDP predicate matches the <em>local port</em> against a wildcard-or-exact address and
/// accepts the PID only when exactly one distinct PID matches; the TCP predicate requires all four
/// tuple fields. That asymmetry is why only TCP rows may be served from a snapshot older than the
/// caller's request (see <see cref="ProcessOwnerTableCache"/>).
/// </para>
/// <para>
/// The row storage is reused across reads so a scan allocates no row array and no framework
/// address: rows carry <see cref="IPAddressValue"/> by value, and the arrays grow to the widest
/// table seen and are then never replaced. A fill invalidates the slot first
/// (<see cref="BeginTcpFill"/>) and publishes it last (<see cref="CompleteFill"/>), so a failed or
/// partial parse can never leave a mixture of two tables searchable.
/// </para>
/// </summary>
internal sealed class OwnerTable
{
    private UdpOwnerRow[] _udpRows = [];
    private TcpOwnerRow[] _tcpRows = [];
    private int _udpCount;
    private int _tcpCount;

    public OwnerTable(OwnerTableKind kind)
        : this(kind, fillable: true)
    {
    }

    private OwnerTable(OwnerTableKind kind, bool fillable)
    {
        Fillable = fillable;
        Family = FamilyOf(kind);
        // The IPv6 wildcard is the zero address with no scope; IPv4 has a precomputed constant.
        Wildcard = Family == AddressFamilyKind.IPv4 ? IPAddressValue.IPv4Any : new IPAddressValue(0, AddressFamilyKind.IPv6);
    }

    /// <summary>
    /// Builds a slot from already parsed rows. The arrays are copied, so the caller keeps
    /// ownership of them; this is the cold shape the scripted test readers use.
    /// </summary>
    public OwnerTable(OwnerTableKind kind, UdpOwnerRow[] udpRows, TcpOwnerRow[] tcpRows)
        : this(kind)
    {
        ArgumentNullException.ThrowIfNull(udpRows);
        ArgumentNullException.ThrowIfNull(tcpRows);
        udpRows.AsSpan().CopyTo(BeginUdpFill(udpRows.Length));
        tcpRows.AsSpan().CopyTo(BeginTcpFill(tcpRows.Length));
        CompleteFill();
    }

    /// <summary>
    /// The "this platform has no owner tables" answer: unavailable, so no lookup can match and no
    /// caller may cache it as a positive result.
    /// </summary>
    public static OwnerTable Unavailable { get; } = new(OwnerTableKind.Tcp4, fillable: false);

    /// <summary>False for <see cref="Unavailable"/> and while a fill is in progress: no row storage
    /// is searchable, so no answer may be cached.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>The wildcard address of this slot's family; rows bound to any local address report it.</summary>
    private IPAddressValue Wildcard { get; }

    /// <summary>False only for the shared <see cref="Unavailable"/> constant, which no reader may fill.</summary>
    private bool Fillable { get; }

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

    private AddressFamilyKind Family { get; }

    /// <summary>
    /// Starts a UDP fill and returns the span the reader writes exactly <paramref name="rowCount"/>
    /// rows into. The slot is unavailable until <see cref="CompleteFill"/>, so an interrupted fill
    /// is never searchable.
    /// </summary>
    internal Span<UdpOwnerRow> BeginUdpFill(int rowCount)
    {
        AssertFillable();
        if (_udpRows.Length < rowCount) _udpRows = new UdpOwnerRow[GrowCapacity(_udpRows.Length, rowCount)];
        _udpCount = rowCount;
        IsAvailable = false;
        return _udpRows.AsSpan(0, rowCount);
    }

    /// <summary>Starts a TCP fill; see <see cref="BeginUdpFill"/>.</summary>
    internal Span<TcpOwnerRow> BeginTcpFill(int rowCount)
    {
        AssertFillable();
        if (_tcpRows.Length < rowCount) _tcpRows = new TcpOwnerRow[GrowCapacity(_tcpRows.Length, rowCount)];
        _tcpCount = rowCount;
        IsAvailable = false;
        return _tcpRows.AsSpan(0, rowCount);
    }

    /// <summary>Publishes a completed fill; every row of the started fill must be written first.</summary>
    internal void CompleteFill() => IsAvailable = true;

    // The shared "no table" constant is handed to every caller that must answer without a read, so
    // it must never become a slot: filling it would make one caller's rows visible to all of them.
    private void AssertFillable()
    {
        if (!Fillable) throw new InvalidOperationException("The shared unavailable owner table cannot be filled.");
    }

    // Doubling keeps the array replacement count logarithmic, so a table that grows by a few rows at
    // a time stops allocating after the first few scans instead of on every scan.
    private static int GrowCapacity(int current, int required) => Math.Max(required, current == 0 ? 64 : current * 2);

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
        foreach (var row in _udpRows.AsSpan(0, _udpCount))
        {
            if (row.Port != local.Port) continue;
            if (!row.Address.Equals(Wildcard) && !row.Address.Equals(localAddress)) continue;
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
        foreach (var row in _tcpRows.AsSpan(0, _tcpCount))
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
