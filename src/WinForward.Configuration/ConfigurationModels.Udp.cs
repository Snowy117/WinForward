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
}

public sealed partial record ValidatedConfiguration
{
    /// <summary>
    /// How long an idle UDP session is retained before its relay socket and SOCKS5 control
    /// connection are released; the sweeper derives its UDP sweep cadence from this value.
    /// </summary>
    public TimeSpan UdpSessionIdleTimeout { get; init; } = ConfigurationLoader.DefaultUdpSessionIdleTimeout;
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
}
