using System.Text.Json;
using System.Text.Json.Serialization;
using WinForward.Core;

namespace WinForward.Configuration;

public sealed partial class WinForwardConfigDto
{
    [JsonPropertyName("logLevel")]
    public JsonElement LogLevel { get; init; }

    [JsonPropertyName("socks5Servers")]
    public IReadOnlyList<Socks5ServerDto?>? Socks5Servers { get; init; }

    [JsonPropertyName("localTargets")]
    public IReadOnlyList<LocalTargetDto?>? LocalTargets { get; init; }

    [JsonPropertyName("host")]
    public RuleDomainDto? Host { get; init; }

    [JsonPropertyName("forwarded")]
    public RuleDomainDto? Forwarded { get; init; }

    [JsonPropertyName("proxyUnavailableAction")]
    public string? ProxyUnavailableAction { get; init; }

    [JsonPropertyName("processingFailureAction")]
    public string? ProcessingFailureAction { get; init; }

    [JsonPropertyName("tcpFlowCapacity")]
    public int? TcpFlowCapacity { get; init; }

    [JsonPropertyName("setupWorkerCount")]
    public int? SetupWorkerCount { get; init; }
}

public sealed class RuleDomainDto
{
    [JsonPropertyName("fallbackAction")]
    public string? FallbackAction { get; init; }

    [JsonPropertyName("rules")]
    public IReadOnlyList<RuleDto?>? Rules { get; init; }
}

public sealed class Socks5ServerDto
{
    public string? Name { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
}

public sealed class LocalTargetDto
{
    public string? Name { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; }
}

public sealed class RuleDto
{
    public string?[]? Process { get; init; }
    public string?[]? AdapterId { get; init; }
    public string?[]? AdapterName { get; init; }
    public string?[]? Protocol { get; init; }
    public string?[]? AddressFamily { get; init; }
    public string?[]? RemoteCidr { get; init; }
    public string?[]? RemotePort { get; init; }
    public string? Action { get; init; }

    /// <summary>The named target a <c>proxy</c> action selects.</summary>
    [JsonPropertyName("target")]
    public string? Target { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(WinForwardConfigDto))]
public partial class ConfigurationJsonContext : JsonSerializerContext;

public sealed record Socks5Server(
    string Name,
    string Host,
    ushort Port,
    string? Username,
    string? Password);

/// <summary>
/// A named endpoint on this host that terminates a selected flow, declared in the
/// <c>localTargets</c> list: the flow's payload is forwarded to <see cref="Endpoint"/> verbatim and
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

public enum RuntimeLogLevel
{
    Error,
    Warn,
    Info,
    Debug,
    Trace,
}

public sealed partial record ValidatedConfiguration(
    IReadOnlyDictionary<string, ProxyTarget> Targets,
    PolicySnapshot Policy,
    RuntimeLogLevel LogLevel = RuntimeLogLevel.Info,
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
    /// <summary>The default concurrent proxied TCP flow budget: 16,384 ephemeral ports x 50% headroom / 2 ports per flow.</summary>
    public const int DefaultTcpFlowCapacity = 4_096;

    public static bool TryParse(string json, out WinForwardConfigDto? dto, out IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        try
        {
            dto = JsonSerializer.Deserialize(json, ConfigurationJsonContext.Default.WinForwardConfigDto);
            if (dto is null)
            {
                diagnostics = [new ConfigDiagnostic("$", "Configuration must be a JSON object.")];
            }
            else if (dto.LogLevel.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.String or JsonValueKind.Null))
            {
                diagnostics = [new ConfigDiagnostic("logLevel", "Log level must be a string.")];
            }
            else
            {
                diagnostics = [];
            }
            return diagnostics.Count == 0;
        }
        catch (JsonException exception)
        {
            dto = null;
            var path = string.IsNullOrWhiteSpace(exception.Path) ? "$" : exception.Path!;
            diagnostics = [new ConfigDiagnostic(path, "Invalid JSON configuration.")];
            return false;
        }
    }

    public static bool TryValidate(WinForwardConfigDto dto, out ValidatedConfiguration? configuration, out IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        var errors = new List<ConfigDiagnostic>();
        var warnings = new List<ConfigDiagnostic>();
        var targets = new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase);
        var logLevel = ParseLogLevel(dto, errors);
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
            logLevel,
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

    private static RuntimeLogLevel ParseLogLevel(WinForwardConfigDto dto, List<ConfigDiagnostic> errors)
    {
        if (dto.LogLevel.ValueKind == JsonValueKind.Undefined) return RuntimeLogLevel.Info;
        if (dto.LogLevel.ValueKind != JsonValueKind.String)
        {
            errors.Add(new("logLevel", "Log level must be error, warn, info, debug, or trace."));
            return RuntimeLogLevel.Info;
        }

        var value = dto.LogLevel.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new("logLevel", "Log level must be error, warn, info, debug, or trace."));
            return RuntimeLogLevel.Info;
        }

        var level = value.Trim().ToLowerInvariant() switch
        {
            "error" => RuntimeLogLevel.Error,
            "warn" => RuntimeLogLevel.Warn,
            "info" => RuntimeLogLevel.Info,
            "debug" => RuntimeLogLevel.Debug,
            "trace" => RuntimeLogLevel.Trace,
            _ => (RuntimeLogLevel?)null,
        };
        if (level is not null) return level.Value;
        errors.Add(new("logLevel", "Log level must be error, warn, info, debug, or trace."));
        return RuntimeLogLevel.Info;
    }

    private static void ValidateFailureActions(WinForwardConfigDto dto, List<ConfigDiagnostic> errors)
    {
        ValidateFailureAction(dto.ProxyUnavailableAction, "proxyUnavailableAction", errors);
        ValidateFailureAction(dto.ProcessingFailureAction, "processingFailureAction", errors);
    }

    private static void ValidateFailureAction(string? raw, string path, List<ConfigDiagnostic> errors)
    {
        if (raw is not null && !string.Equals(raw.Trim(), "block", StringComparison.OrdinalIgnoreCase)) errors.Add(new(path, "Only block is supported in the first release."));
    }
}
