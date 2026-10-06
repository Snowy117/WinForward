using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinForward.Core;

namespace WinForward.Configuration;

public sealed partial class WinForwardConfigDto
{
    [JsonPropertyName("Socks5Servers")]
    public IReadOnlyList<Socks5ServerDto?>? Socks5Servers { get; init; }

    [JsonPropertyName("LocalTargets")]
    public IReadOnlyList<LocalTargetDto?>? LocalTargets { get; init; }

    [JsonPropertyName("Host")]
    public RuleDomainDto? Host { get; init; }

    [JsonPropertyName("Forwarded")]
    public RuleDomainDto? Forwarded { get; init; }

    [JsonPropertyName("ProxyUnavailableAction")]
    public string? ProxyUnavailableAction { get; init; }

    [JsonPropertyName("ProcessingFailureAction")]
    public string? ProcessingFailureAction { get; init; }

    [JsonPropertyName("TcpFlowCapacity")]
    public int? TcpFlowCapacity { get; init; }

    [JsonPropertyName("SetupWorkerCount")]
    public int? SetupWorkerCount { get; init; }
}

public sealed class RuleDomainDto
{
    [JsonPropertyName("FallbackAction")]
    public string? FallbackAction { get; init; }

    [JsonPropertyName("Rules")]
    public IReadOnlyList<RuleDto?>? Rules { get; init; }
}

public sealed class Socks5ServerDto
{
    [JsonPropertyName("Name")]
    public string? Name { get; init; }

    [JsonPropertyName("Host")]
    public string? Host { get; init; }

    [JsonPropertyName("Port")]
    public int Port { get; init; }

    [JsonPropertyName("Username")]
    public string? Username { get; init; }

    [JsonPropertyName("Password")]
    public string? Password { get; init; }

    [JsonPropertyName("UdpOverTcp")]
    public bool? UdpOverTcp { get; init; }
}

public sealed class LocalTargetDto
{
    [JsonPropertyName("Name")]
    public string? Name { get; init; }

    [JsonPropertyName("Host")]
    public string? Host { get; init; }

    [JsonPropertyName("Port")]
    public int Port { get; init; }
}

public sealed class RuleDto
{
    [JsonPropertyName("Process")]
    public string?[]? Process { get; init; }

    [JsonPropertyName("AdapterId")]
    public string?[]? AdapterId { get; init; }

    [JsonPropertyName("AdapterName")]
    public string?[]? AdapterName { get; init; }

    [JsonPropertyName("Protocol")]
    public string?[]? Protocol { get; init; }

    [JsonPropertyName("AddressFamily")]
    public string?[]? AddressFamily { get; init; }

    [JsonPropertyName("RemoteCidr")]
    public string?[]? RemoteCidr { get; init; }

    [JsonPropertyName("RemotePort")]
    public string?[]? RemotePort { get; init; }

    [JsonPropertyName("Action")]
    public string? Action { get; init; }

    /// <summary>The named target a <c>proxy</c> action selects.</summary>
    [JsonPropertyName("Target")]
    public string? Target { get; init; }
}

/// <summary>
/// The strict reader for the <c>WinForward</c> section. Every member names its own key — no naming
/// policy infers one — and an unknown or wrongly cased key is an unmapped member, so the two
/// failures that <c>IConfiguration</c>'s case-insensitive, key-ignoring store would swallow stay
/// hard errors here.
/// </summary>
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(WinForwardConfigDto))]
public partial class ConfigurationJsonContext : JsonSerializerContext;

/// <summary>
/// A configured SOCKS5 server. <see cref="UdpOverTcp"/> selects the opt-in UoT v2 connect-mode
/// carriage for the UDP flows this server serves; the native SOCKS5 UDP relay remains the default.
/// </summary>
public sealed record Socks5Server(
    string Name,
    string Host,
    ushort Port,
    string? Username,
    string? Password,
    bool UdpOverTcp = false);

/// <summary>
/// A named endpoint on this host that terminates a selected flow, declared in the
/// <c>LocalTargets</c> list: the flow's payload is forwarded to <see cref="Endpoint"/> verbatim and
/// the endpoint's replies are attributed to the flow's original destination. Nothing in the packet
/// path learns what protocol the payload carries.
/// </summary>
public sealed record LocalTarget(string Name, Endpoint Endpoint);

/// <summary>
/// The target a proxy decision resolved to: exactly one of a SOCKS5 server (<see cref="Socks5"/>)
/// or a local endpoint (<see cref="Local"/>), under the configured <see cref="Name"/> (one
/// namespace across both kinds). A readonly record struct, so resolving a target stays one
/// dictionary probe plus a value copy and adds no allocation to the packet path.
/// </summary>
public readonly record struct ProxyTarget(string Name, Socks5Server? Socks5, LocalTarget? Local)
{
    /// <summary>True when this target is a local endpoint rather than a SOCKS5 server.</summary>
    public bool IsLocal => Local is not null;

    /// <summary>Wraps a resolved SOCKS5 server as its own target.</summary>
    public static ProxyTarget FromServer(Socks5Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return new ProxyTarget(server.Name, server, Local: null);
    }
}

public sealed record ConfigDiagnostic(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

public sealed partial record ValidatedConfiguration(
    IReadOnlyDictionary<string, ProxyTarget> Targets,
    PolicySnapshot Policy,
    bool IncludeProcessPathInLogs = false,
    int TcpFlowCapacity = ConfigurationLoader.DefaultTcpFlowCapacity,
    int SetupWorkerCount = 0,
    int UdpSessionCapacity = ConfigurationLoader.DefaultUdpSessionCapacity,
    int UdpRelayReceiveBufferBytes = ConfigurationLoader.DefaultUdpRelayReceiveBufferBytes)
{
    /// <summary>
    /// Non-blocking validation findings (for example a tcpFlowCapacity above the warning
    /// threshold) surfaced alongside an otherwise valid configuration.
    /// </summary>
    public IReadOnlyList<ConfigDiagnostic> Warnings { get; init; } = [];
}

public static partial class ConfigurationLoader
{
    /// <summary>The one top-level section WinForward's own settings live under, beside <c>Logging</c>.</summary>
    public const string SectionName = "WinForward";

    /// <summary>The default concurrent proxied TCP flow budget: 16,384 ephemeral ports x 50% headroom / 2 ports per flow.</summary>
    public const int DefaultTcpFlowCapacity = 4_096;

    public static bool TryParse(string json, [NotNullWhen(true)] out WinForwardConfigDto? dto, out IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        try
        {
            dto = JsonSerializer.Deserialize(json, ConfigurationJsonContext.Default.WinForwardConfigDto);
            diagnostics = dto is null ? [new ConfigDiagnostic(SectionName, "Configuration must be a JSON object.")] : [];
            return diagnostics.Count == 0;
        }
        catch (JsonException exception)
        {
            dto = null;
            diagnostics = [new ConfigDiagnostic(RootedPath(exception.Path), "Invalid JSON configuration.")];
            return false;
        }
    }

    public static bool TryValidate(WinForwardConfigDto dto, [NotNullWhen(true)] out ValidatedConfiguration? configuration, out IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        var errors = new List<ConfigDiagnostic>();
        var warnings = new List<ConfigDiagnostic>();
        var targets = new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase);
        var limits = ConfigurationLimits.Parse(dto, errors, warnings);

        ConfigurationTargets.Validate(dto, targets, errors, warnings);

        var hostRules = new List<PolicyRule>();
        var forwardedRules = new List<PolicyRule>();
        var hostFallback = ConfigurationRules.ParseDomain(dto.Host, FlowOriginKind.Host, targets, hostRules, errors);
        var forwardedFallback = ConfigurationRules.ParseDomain(dto.Forwarded, FlowOriginKind.Forwarded, targets, forwardedRules, errors);

        ValidateFailureActions(dto, errors);

        if (errors.Count > 0 || hostFallback is null)
        {
            configuration = null;
            diagnostics = errors;
            return false;
        }

        configuration = new ValidatedConfiguration(
            targets,
            new PolicySnapshot(hostRules, hostFallback.Value)
            {
                ForwardedRules = forwardedRules,
                ForwardedFallbackAction = forwardedFallback ?? FlowAction.Pass,
            },
            ConfigurationRules.AnyProcessSelectorIsAPath(hostRules),
            limits.TcpFlowCapacity,
            limits.SetupWorkerCount,
            limits.UdpSessionCapacity,
            limits.UdpRelayReceiveBufferBytes)
        {
            Warnings = warnings,
            UdpSessionIdleTimeout = limits.UdpSessionIdleTimeout,
        };
        diagnostics = [];
        return true;
    }

    /// <summary>
    /// Re-roots a JSON path from the deserialised document at the section the document was
    /// materialised from, so every diagnostic names a location in the operator's file.
    /// </summary>
    private static string RootedPath(string? jsonPath) => jsonPath switch
    {
        null or "$" => SectionName,
        _ => SectionName + jsonPath[1..],
    };

    private static void ValidateFailureActions(WinForwardConfigDto dto, List<ConfigDiagnostic> errors)
    {
        ValidateFailureAction(dto.ProxyUnavailableAction, "WinForward.ProxyUnavailableAction", errors);
        ValidateFailureAction(dto.ProcessingFailureAction, "WinForward.ProcessingFailureAction", errors);
    }

    private static void ValidateFailureAction(string? raw, string path, List<ConfigDiagnostic> errors)
    {
        if (raw is not null && !string.Equals(raw.Trim(), "block", StringComparison.OrdinalIgnoreCase)) errors.Add(new(path, "Only block is supported in the first release."));
    }
}
