using System.Text.Json.Serialization;

namespace WinForward.Configuration;

public sealed partial class WinForwardConfigDto
{
    [JsonPropertyName("udpSessionCapacity")]
    public int? UdpSessionCapacity { get; init; }

    [JsonPropertyName("udpRelayReceiveBufferKb")]
    public int? UdpRelayReceiveBufferKb { get; init; }

    [JsonPropertyName("udpSessionIdleSeconds")]
    public int? UdpSessionIdleSeconds { get; init; }

    [JsonPropertyName("udpAssociationReuse")]
    public string? UdpAssociationReuse { get; init; }

    [JsonPropertyName("udpAssociationMaxPerServer")]
    public int? UdpAssociationMaxPerServer { get; init; }

    [JsonPropertyName("udpAssociationFlowsPerAssociation")]
    public int? UdpAssociationFlowsPerAssociation { get; init; }
}

public sealed partial record ValidatedConfiguration
{
    /// <summary>
    /// How long an idle UDP session is retained before its relay socket and SOCKS5 control
    /// connection are released; the sweeper derives its UDP sweep cadence from this value.
    /// </summary>
    public TimeSpan UdpSessionIdleTimeout { get; init; } = ConfigurationLoader.DefaultUdpSessionIdleTimeout;

    /// <summary>
    /// Whether many UDP flows share one authenticated SOCKS5 association. <c>auto</c> (the default)
    /// shares and passively falls back to per-flow associations for a server observed to pin one
    /// client source port per association; <c>always</c> shares with detection disabled; <c>off</c>
    /// reproduces per-flow associations exactly.
    /// </summary>
    public UdpAssociationReuseMode UdpAssociationReuse { get; init; } = UdpAssociationReuseMode.Auto;

    /// <summary>
    /// The ceiling on shared associations per server (<c>udpAssociationMaxPerServer</c>): a
    /// per-server connection ceiling, not a preallocation — the pool opens only the associations
    /// placement needs. Ascending the product with <see cref="UdpAssociationFlowsPerAssociation"/>
    /// gives the shared head, which the coordinator's own <c>udpSessionCapacity</c> then caps.
    /// </summary>
    public int UdpAssociationMaxPerServer { get; init; } = ConfigurationLoader.DefaultUdpAssociationMaxPerServer;

    /// <summary>
    /// The concurrent flows one shared association serves
    /// (<c>udpAssociationFlowsPerAssociation</c>): the blast radius of an association death and the
    /// capability sampler's live-evidence set. Ascending the product with
    /// <see cref="UdpAssociationMaxPerServer"/> gives the shared head, which the coordinator's own
    /// <c>udpSessionCapacity</c> then caps.
    /// </summary>
    public int UdpAssociationFlowsPerAssociation { get; init; } = ConfigurationLoader.DefaultUdpAssociationFlowsPerAssociation;
}

public static partial class ConfigurationLoader
{
    /// <summary>The default concurrent UDP session budget, unchanged from the historical hard-coded bound.</summary>
    public const int DefaultUdpSessionCapacity = 16_384;

    /// <summary>The default per-session relay socket receive buffer in KiB (matches <c>Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize</c>).</summary>
    public const int DefaultUdpRelayReceiveBufferKb = 64;

    /// <summary>The default per-session relay socket receive buffer in bytes.</summary>
    public const int DefaultUdpRelayReceiveBufferBytes = DefaultUdpRelayReceiveBufferKb * 1_024;

    /// <summary>The default UDP session idle timeout; short enough that the steady-state footprint follows the active flow set.</summary>
    public static readonly TimeSpan DefaultUdpSessionIdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The default ceiling on shared associations per server. A ceiling, not a preallocation: the
    /// pool opens only the associations placement needs, and exceeding it falls back to per-flow
    /// associations instead of refusing a flow. 1,024 multiplied by
    /// <see cref="DefaultUdpAssociationFlowsPerAssociation"/> covers
    /// <see cref="DefaultUdpSessionCapacity"/> — every flow the coordinator admits can be shared at
    /// the default retention and arrival rate.
    /// </summary>
    public const int DefaultUdpAssociationMaxPerServer = 1_024;

    /// <summary>
    /// The default number of concurrent flows one shared association serves. Deliberately the
    /// surgical knob: it is the blast radius of an association death and the capability sampler's
    /// live-evidence set, so it stays small while the per-server ceiling above carries the head.
    /// </summary>
    public const int DefaultUdpAssociationFlowsPerAssociation = 16;

    /// <summary>
    /// Normalizes the optional udpAssociationReuse key: omitted means <c>auto</c>, an unknown
    /// value is a validation error (the configuration is rejected) and falls back to <c>auto</c>.
    /// </summary>
    private static UdpAssociationReuseMode ParseAssociationReuse(string? raw, List<ConfigDiagnostic> errors)
    {
        if (raw is null) return UdpAssociationReuseMode.Auto;
        var mode = raw.Trim().ToLowerInvariant() switch
        {
            "auto" => UdpAssociationReuseMode.Auto,
            "always" => UdpAssociationReuseMode.Always,
            "off" => UdpAssociationReuseMode.Off,
            _ => (UdpAssociationReuseMode?)null,
        };
        if (mode is not null) return mode.Value;
        errors.Add(new("udpAssociationReuse", "Association reuse must be auto, always, or off."));
        return UdpAssociationReuseMode.Auto;
    }
}
