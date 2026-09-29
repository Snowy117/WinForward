using System.Globalization;
using System.Runtime.InteropServices;

namespace WinForward.Configuration;

/// <summary>The normalized numeric limits one validation pass produces; see <see cref="ConfigurationLimits"/>.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct ConfigurationLimitValues(
    int TcpFlowCapacity,
    int SetupWorkerCount,
    int UdpSessionCapacity,
    int UdpRelayReceiveBufferBytes,
    TimeSpan UdpSessionIdleTimeout,
    int UdpAssociationMaxPerServer,
    int UdpAssociationFlowsPerAssociation);

/// <summary>
/// Bounds, warning thresholds, and diagnostics for the numeric configuration limits. The loader
/// keeps the public defaults and the object graph; this type owns the range checks that normalize
/// each optional value. Every parser follows the same fail-closed shape — omitted means the
/// default, out-of-range is an error that returns the default, and a documented threshold
/// collects a non-blocking warning.
/// </summary>
internal static class ConfigurationLimits
{
    /// <summary>The smallest accepted tcpFlowCapacity; a zero or negative budget would block every flow.</summary>
    private const int MinimumTcpFlowCapacity = 1;
    /// <summary>The largest accepted tcpFlowCapacity; above this the budget offers no port-pool protection at all.</summary>
    private const int MaximumTcpFlowCapacity = 8_192;
    /// <summary>Values above the default warn during validation because they shrink the reserved ephemeral-port headroom.</summary>
    private const int TcpFlowCapacityWarningThreshold = 4_096;
    /// <summary>The smallest accepted setupWorkerCount; at least one worker must exist to drain new-flow setup.</summary>
    private const int MinimumSetupWorkerCount = 1;
    /// <summary>The largest accepted setupWorkerCount; beyond this the dedicated threads outweigh any setup throughput gain.</summary>
    private const int MaximumSetupWorkerCount = 256;

    /// <summary>The smallest accepted udpSessionCapacity; a zero or negative budget would block every UDP flow.</summary>
    private const int MinimumUdpSessionCapacity = 1;
    /// <summary>The largest accepted udpSessionCapacity; it is the historical hard-coded bound.</summary>
    private const int MaximumUdpSessionCapacity = 16_384;
    /// <summary>
    /// Values above this warn during validation: like a proxied TCP flow, each UDP session consumes
    /// two local ports (the SOCKS5 control connection and the relay socket), so a larger budget
    /// eats the ephemeral-port headroom.
    /// </summary>
    private const int UdpSessionCapacityWarningThreshold = 4_096;

    /// <summary>The smallest accepted udpRelayReceiveBufferKb; below this a response burst overflows the relay socket.</summary>
    private const int MinimumUdpRelayReceiveBufferKb = 16;
    /// <summary>The largest accepted udpRelayReceiveBufferKb; above this one socket's kernel buffer outweighs its forwarding value.</summary>
    private const int MaximumUdpRelayReceiveBufferKb = 1_024;
    /// <summary>The per-socket receive-buffer warning threshold in KiB; the aggregate is checked against the session capacity too.</summary>
    private const int UdpRelayReceiveBufferKbWarningThreshold = 256;
    /// <summary>The session capacity above which a large per-socket receive buffer warns: buffers multiply by concurrent sessions.</summary>
    private const int UdpRelayReceiveBufferWarningSessionCapacity = 2_048;

    /// <summary>The smallest accepted udpSessionIdleSeconds.</summary>
    private const int MinimumUdpSessionIdleSeconds = 5;
    /// <summary>The largest accepted udpSessionIdleSeconds.</summary>
    private const int MaximumUdpSessionIdleSeconds = 600;

    /// <summary>The smallest accepted udpAssociationMaxPerServer; a zero ceiling would disable shared associations entirely.</summary>
    private const int MinimumUdpAssociationMaxPerServer = 1;
    /// <summary>The largest accepted udpAssociationMaxPerServer; beyond it the per-server ceiling stops being a bound an operator can reason about.</summary>
    private const int MaximumUdpAssociationMaxPerServer = 16_384;
    /// <summary>The smallest accepted udpAssociationFlowsPerAssociation; a zero bound would refuse every shared placement.</summary>
    private const int MinimumUdpAssociationFlowsPerAssociation = 1;
    /// <summary>
    /// The largest accepted udpAssociationFlowsPerAssociation. It is the blast radius of one
    /// association death and the capability sampler's live-evidence set, so the accepted ceiling
    /// stays far below the session capacity on purpose.
    /// </summary>
    private const int MaximumUdpAssociationFlowsPerAssociation = 256;

    /// <summary>
    /// Normalizes every numeric limit in one pass. The per-session relay receive buffer is
    /// normalized before the UDP session capacity because the capacity's warning sentence names the
    /// aggregate kernel receive buffer that only both validated values compute; the buffer's own
    /// cross-value warning is emitted once both are validated, and the association head's
    /// cross-value warning once both association bounds are.
    /// </summary>
    internal static ConfigurationLimitValues Parse(WinForwardConfigDto dto, List<ConfigDiagnostic> errors, List<ConfigDiagnostic> warnings)
    {
        var udpRelayReceiveBufferKb = ParseUdpRelayReceiveBufferKb(dto, errors);
        var udpSessionCapacity = ParseUdpSessionCapacity(dto, udpRelayReceiveBufferKb, errors, warnings);
        WarnOnAggregateRelayReceiveBuffer(udpRelayReceiveBufferKb, udpSessionCapacity, warnings);
        var udpAssociationMaxPerServer = ParseUdpAssociationMaxPerServer(dto, errors);
        var udpAssociationFlowsPerAssociation = ParseUdpAssociationFlowsPerAssociation(dto, errors);
        WarnOnAssociationHeadBelowSessionCapacity(udpSessionCapacity, udpAssociationMaxPerServer, udpAssociationFlowsPerAssociation, warnings);
        return new(
            ParseTcpFlowCapacity(dto, errors, warnings),
            ParseSetupWorkerCount(dto, errors),
            udpSessionCapacity,
            udpRelayReceiveBufferKb * 1_024,
            ParseUdpSessionIdleTimeout(dto, errors),
            udpAssociationMaxPerServer,
            udpAssociationFlowsPerAssociation);
    }

    /// <summary>
    /// Normalizes the optional tcpFlowCapacity budget. Omitted values fall back to the default;
    /// out-of-range values are rejected with the accepted range, and values above the default
    /// collect a non-blocking warning because they shrink the reserved ephemeral-port headroom.
    /// </summary>
    private static int ParseTcpFlowCapacity(WinForwardConfigDto dto, List<ConfigDiagnostic> errors, List<ConfigDiagnostic> warnings)
    {
        if (dto.TcpFlowCapacity is not { } value) return ConfigurationLoader.DefaultTcpFlowCapacity;
        // ReSharper disable once ConvertIfStatementToSwitchStatement // Range-pattern precondition — a switch over the same value with a relational pattern adds ceremony and hides the fail-closed order (report the error, return the default) that pairs with the threshold warning below.
        if (value is < MinimumTcpFlowCapacity or > MaximumTcpFlowCapacity)
        {
            errors.Add(new("tcpFlowCapacity", $"TCP flow capacity must be in {MinimumTcpFlowCapacity}..{MaximumTcpFlowCapacity}."));
            return ConfigurationLoader.DefaultTcpFlowCapacity;
        }
        if (value > TcpFlowCapacityWarningThreshold)
        {
            warnings.Add(new("tcpFlowCapacity", $"Values above {TcpFlowCapacityWarningThreshold} leave less ephemeral-port headroom; each proxied TCP flow consumes 2 local ports."));
        }
        return value;
    }

    /// <summary>
    /// Normalizes the optional setupWorkerCount override. An omitted value means auto (the executor's
    /// 2x-logical-processor default); out-of-range values are rejected.
    /// </summary>
    private static int ParseSetupWorkerCount(WinForwardConfigDto dto, List<ConfigDiagnostic> errors)
    {
        if (dto.SetupWorkerCount is not { } value) return 0;
        if (value is < MinimumSetupWorkerCount or > MaximumSetupWorkerCount)
        {
            errors.Add(new("setupWorkerCount", $"Setup worker count must be in {MinimumSetupWorkerCount}..{MaximumSetupWorkerCount}."));
            return 0;
        }
        return value;
    }

    /// <summary>
    /// Normalizes the optional udpSessionCapacity budget (R4): the bound on concurrent UDP
    /// sessions. Omitted values keep the historical default; out-of-range values are rejected, and
    /// values above the default collect the ephemeral-port warning the TCP budget carries (each UDP
    /// session also consumes 2 local ports), extended with the aggregate kernel receive buffer the
    /// validated per-session buffer multiplies into — the combination that is otherwise silent, for
    /// example the 128 KiB default times a raised capacity.
    /// </summary>
    private static int ParseUdpSessionCapacity(WinForwardConfigDto dto, int relayReceiveBufferKb, List<ConfigDiagnostic> errors, List<ConfigDiagnostic> warnings)
    {
        if (dto.UdpSessionCapacity is not { } value) return ConfigurationLoader.DefaultUdpSessionCapacity;
        // ReSharper disable once ConvertIfStatementToSwitchStatement // Range-pattern precondition — a switch over the same value with a relational pattern adds ceremony and hides the fail-closed order (report the error, return the default) that pairs with the threshold warning below.
        if (value is < MinimumUdpSessionCapacity or > MaximumUdpSessionCapacity)
        {
            errors.Add(new("udpSessionCapacity", $"UDP session capacity must be in {MinimumUdpSessionCapacity}..{MaximumUdpSessionCapacity}."));
            return ConfigurationLoader.DefaultUdpSessionCapacity;
        }
        if (value > UdpSessionCapacityWarningThreshold)
        {
            var aggregateMiB = (long)value * relayReceiveBufferKb / 1_024;
            warnings.Add(new("udpSessionCapacity", string.Create(CultureInfo.InvariantCulture, $"Values above {UdpSessionCapacityWarningThreshold} leave less ephemeral-port headroom; each proxied UDP session consumes 2 local ports and up to {relayReceiveBufferKb} KiB of kernel receive buffer ({aggregateMiB} MiB at this capacity).")));
        }
        return value;
    }

    /// <summary>
    /// Normalizes the optional udpRelayReceiveBufferKb value (R4): the per-session relay socket
    /// buffer, bounded because it multiplies by the concurrent session count. Omitted values fall
    /// back to the default; out-of-range values are rejected with the accepted range. The aggregate
    /// kernel-memory warnings live in <see cref="Parse"/> and
    /// <see cref="WarnOnAggregateRelayReceiveBuffer"/>, which also need the validated capacity.
    /// </summary>
    private static int ParseUdpRelayReceiveBufferKb(WinForwardConfigDto dto, List<ConfigDiagnostic> errors)
    {
        if (dto.UdpRelayReceiveBufferKb is not { } value) return ConfigurationLoader.DefaultUdpRelayReceiveBufferKb;
        // ReSharper disable once ConvertIfStatementToSwitchStatement // Range-pattern precondition — a switch over the same value with a relational pattern adds ceremony and hides the fail-closed order (report the error, return the default) that pairs with the aggregate threshold warning below.
        if (value is < MinimumUdpRelayReceiveBufferKb or > MaximumUdpRelayReceiveBufferKb)
        {
            errors.Add(new("udpRelayReceiveBufferKb", $"UDP relay receive buffer must be in {MinimumUdpRelayReceiveBufferKb}..{MaximumUdpRelayReceiveBufferKb} KiB."));
            return ConfigurationLoader.DefaultUdpRelayReceiveBufferKb;
        }
        return value;
    }

    /// <summary>
    /// The buffer-vs-capacity half of the kernel-memory guard: a per-session buffer above
    /// <see cref="UdpRelayReceiveBufferKbWarningThreshold"/> KiB multiplied by more than
    /// <see cref="UdpRelayReceiveBufferWarningSessionCapacity"/> sessions exceeds ~512 MiB. The
    /// capacity warning names the aggregate at every capacity above the default, so this fires the
    /// extra alarm only when the per-session value is itself exceptional.
    /// </summary>
    private static void WarnOnAggregateRelayReceiveBuffer(int relayReceiveBufferKb, int sessionCapacity, List<ConfigDiagnostic> warnings)
    {
        if (relayReceiveBufferKb > UdpRelayReceiveBufferKbWarningThreshold && sessionCapacity > UdpRelayReceiveBufferWarningSessionCapacity)
        {
            warnings.Add(new("udpRelayReceiveBufferKb", $"A per-session receive buffer above {UdpRelayReceiveBufferKbWarningThreshold} KiB multiplies by the concurrent UDP session count; above {UdpRelayReceiveBufferWarningSessionCapacity} sessions the aggregate kernel receive buffer exceeds 512 MiB."));
        }
    }

    /// <summary>
    /// The head-vs-capacity half of the association guard: the two validated caps multiply into the
    /// shared head — the flows one server can serve from shared associations — and the coordinator
    /// admits up to <c>udpSessionCapacity</c> flows. When the product is smaller, the excess flows
    /// are not refused: each is served from its own private association, i.e. its own control
    /// connection, which is invisible in every row except the descriptor and port counts. The
    /// defaults (16 × 1,024 = 16,384) cover the default capacity exactly and stay silent.
    /// </summary>
    private static void WarnOnAssociationHeadBelowSessionCapacity(int sessionCapacity, int maxAssociationsPerServer, int flowsPerAssociation, List<ConfigDiagnostic> warnings)
    {
        var sharedHead = (long)maxAssociationsPerServer * flowsPerAssociation;
        if (sharedHead >= sessionCapacity) return;
        warnings.Add(new("udpAssociationMaxPerServer", string.Create(CultureInfo.InvariantCulture, $"The shared association head is {maxAssociationsPerServer} × {flowsPerAssociation} = {sharedHead} flows per server, below the {sessionCapacity}-flow UDP session capacity: a single server's flows beyond the head are served from private per-flow associations, so the excess each hold their own control connection instead of sharing one.")));
    }

    /// <summary>
    /// Normalizes the optional udpSessionIdleSeconds value (R4): how long an idle UDP session is
    /// retained before the sweeper releases its relay socket and control connection. Omitted values
    /// fall back to the default; out-of-range values are rejected. The sweeper's UDP cadence derives
    /// from this value (half the timeout, at least 5 seconds), so a shorter retention also shortens
    /// the steady-state resource tail.
    /// </summary>
    private static TimeSpan ParseUdpSessionIdleTimeout(WinForwardConfigDto dto, List<ConfigDiagnostic> errors)
    {
        if (dto.UdpSessionIdleSeconds is not { } value) return ConfigurationLoader.DefaultUdpSessionIdleTimeout;
        if (value is < MinimumUdpSessionIdleSeconds or > MaximumUdpSessionIdleSeconds)
        {
            errors.Add(new("udpSessionIdleSeconds", $"UDP session idle timeout must be in {MinimumUdpSessionIdleSeconds}..{MaximumUdpSessionIdleSeconds} seconds."));
            return ConfigurationLoader.DefaultUdpSessionIdleTimeout;
        }
        return TimeSpan.FromSeconds(value);
    }

    /// <summary>
    /// Normalizes the optional udpAssociationMaxPerServer ceiling: how many shared associations one
    /// server may hold. Omitted values fall back to the default; out-of-range values are rejected.
    /// The ceiling is not a preallocation — the pool opens only the associations placement needs —
    /// so there is no threshold warning; the head it multiplies into is bounded by the session
    /// capacity the coordinator admits.
    /// </summary>
    private static int ParseUdpAssociationMaxPerServer(WinForwardConfigDto dto, List<ConfigDiagnostic> errors)
    {
        if (dto.UdpAssociationMaxPerServer is not { } value) return ConfigurationLoader.DefaultUdpAssociationMaxPerServer;
        if (value is < MinimumUdpAssociationMaxPerServer or > MaximumUdpAssociationMaxPerServer)
        {
            errors.Add(new("udpAssociationMaxPerServer", $"UDP association ceiling must be in {MinimumUdpAssociationMaxPerServer}..{MaximumUdpAssociationMaxPerServer} per server."));
            return ConfigurationLoader.DefaultUdpAssociationMaxPerServer;
        }
        return value;
    }

    /// <summary>
    /// Normalizes the optional udpAssociationFlowsPerAssociation bound: how many concurrent flows
    /// one shared association serves, which is also the blast radius of an association death and
    /// the capability sampler's live-evidence set. Omitted values fall back to the default;
    /// out-of-range values are rejected.
    /// </summary>
    private static int ParseUdpAssociationFlowsPerAssociation(WinForwardConfigDto dto, List<ConfigDiagnostic> errors)
    {
        if (dto.UdpAssociationFlowsPerAssociation is not { } value) return ConfigurationLoader.DefaultUdpAssociationFlowsPerAssociation;
        if (value is < MinimumUdpAssociationFlowsPerAssociation or > MaximumUdpAssociationFlowsPerAssociation)
        {
            errors.Add(new("udpAssociationFlowsPerAssociation", $"UDP flows per shared association must be in {MinimumUdpAssociationFlowsPerAssociation}..{MaximumUdpAssociationFlowsPerAssociation}."));
            return ConfigurationLoader.DefaultUdpAssociationFlowsPerAssociation;
        }
        return value;
    }
}
