using System.Runtime.InteropServices;
using WinForward.Core;

namespace WinForward.NdisApi;

/// <summary>The outcome of one static-filter-table operation.</summary>
public enum NdisKernelFilterStatus
{
    /// <summary>The native call succeeded and the driver's table now matches the request.</summary>
    Success,

    /// <summary>
    /// The deployed <c>ndisapi.dll</c> does not export the static-filter API. The feature is
    /// unavailable on this deployment; callers degrade without touching capture.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The table read back does not match the set this driver last installed: another client owns
    /// the driver's static table, or a replace did not land. Distinct from
    /// <see cref="Failed"/> because no native error occurred.
    /// </summary>
    TableMismatch,

    /// <summary>A native call returned FALSE; <c>nativeError</c> carries <c>Marshal.GetLastWin32Error()</c>.</summary>
    Failed,
}

/// <summary>
/// One TCP flow tuple as the static-filter table mirrors it: the two endpoints of a flow in a
/// caller-chosen canonical order. Both addresses must share a family; the table encoder writes two
/// oriented entries per rule (A→B and B→A), so the record itself carries no direction.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NdisTcpFlowRule(IPAddressValue AddressA, ushort PortA, IPAddressValue AddressB, ushort PortB)
{
    /// <summary>
    /// True when this tuple can be mirrored into the native table: both endpoints share an address
    /// family, neither port is zero, and neither endpoint is an IPv6 link-local or scoped address.
    /// The native record has no scope-id field, so a scoped address cannot be expressed and is
    /// refused rather than silently matching the wrong interface.
    /// </summary>
    public bool IsEncodable =>
        AddressA.Family == AddressB.Family
        && PortA != 0
        && PortB != 0
        && AddressA.ScopeId == 0
        && AddressB.ScopeId == 0
        && !IsIPv6LinkLocal(AddressA)
        && !IsIPv6LinkLocal(AddressB);

    /// <summary>True for an address in <c>fe80::/10</c>; always false for IPv4.</summary>
    private static bool IsIPv6LinkLocal(IPAddressValue address) =>
        address.Family == AddressFamilyKind.IPv6 && (address.Bits >> 118) == 0x3FA;
}

/// <summary>
/// A lock-free snapshot of the driver's traffic counters. They are diagnostics only — the summary
/// line derives the per-packet crossing ratio from them — and never influence a disposition.
/// </summary>
/// <param name="ReadCalls">Batched read IOCTLs issued (including the shape self-heal's probe).</param>
/// <param name="PacketsRead">Packets the driver handed to user mode across successful drains.</param>
/// <param name="SendCalls">Reinjection IOCTLs issued, one per single send and per batched chunk.</param>
/// <param name="PacketsReinjected">Packets a successful reinjection call carried.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NdisTrafficCounters(long ReadCalls, long PacketsRead, long SendCalls, long PacketsReinjected);

/// <summary>
/// The driver's static-filter-table seam: replace the whole table, read the per-rule packet counters
/// back, and clear the table. The driver's table is driver-global, not per client handle, so a
/// consumer of this interface claims exclusive ownership of it while enabled.
/// </summary>
/// <remarks>
/// Implementations never throw for unavailability or a native error: <see cref="NdisKernelFilterStatus.Unavailable"/> and
/// <see cref="NdisKernelFilterStatus.Failed"/> are returned so a caller can degrade without touching the capture path. The
/// table is PASS-only by construction — this interface cannot express a drop or redirect action.
/// </remarks>
public interface INdisKernelFilterTable
{
    /// <summary>
    /// Replaces the driver's whole static filter table with two PASS entries per rule. An empty
    /// rule set is refused (the native size expression underflows on a zero-entry table); clearing
    /// is <see cref="Reset"/>.
    /// </summary>
    NdisKernelFilterStatus Replace(ReadOnlySpan<NdisTcpFlowRule> rules, out int nativeError);

    /// <summary>
    /// Reads each rule's cumulative matched-packet count (<c>PacketsIn + PacketsOut</c>) in the same
    /// order the rules were last installed in. A table whose size or contents do not match that
    /// installation returns <see cref="NdisKernelFilterStatus.TableMismatch"/> — the table belongs
    /// to someone else.
    /// </summary>
    NdisKernelFilterStatus ReadRulePacketCounts(ReadOnlySpan<NdisTcpFlowRule> rules, Span<long> packetCounts, out int nativeError);

    /// <summary>Clears the whole static table; the only way to remove rules.</summary>
    NdisKernelFilterStatus Reset(out int nativeError);

    /// <summary>A lock-free snapshot of the read and reinjection traffic counters.</summary>
    NdisTrafficCounters GetTrafficCounters();
}
