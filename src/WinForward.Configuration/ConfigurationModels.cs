using System.Globalization;
using System.Net;
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
    public string? ProxyServer { get; init; }
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
    IReadOnlyDictionary<string, Socks5Server> Servers,
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
        var servers = new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase);
        var logLevel = ParseLogLevel(dto, errors);
        var limits = ConfigurationLimits.Parse(dto, errors, warnings);

        ValidateServers(dto, servers, errors);

        var hostRules = new List<PolicyRule>();
        var forwardedRules = new List<PolicyRule>();
        var hostFallback = ConfigurationRules.ParseDomain(dto.Host, FlowOriginKind.Host, servers, hostRules, errors);
        var forwardedFallback = ConfigurationRules.ParseDomain(dto.Forwarded, FlowOriginKind.Forwarded, servers, forwardedRules, errors);

        ValidateFailureActions(dto, errors);
        var associationReuse = ParseAssociationReuse(dto.UdpAssociationReuse, errors);

        if (errors.Count > 0 || hostFallback is null)
        {
            configuration = null;
            diagnostics = errors;
            return false;
        }

        configuration = new ValidatedConfiguration(
            servers,
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
            UdpAssociationReuse = associationReuse,
            UdpAssociationMaxPerServer = limits.UdpAssociationMaxPerServer,
            UdpAssociationFlowsPerAssociation = limits.UdpAssociationFlowsPerAssociation,
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

    private static void ValidateServers(WinForwardConfigDto dto, Dictionary<string, Socks5Server> servers, List<ConfigDiagnostic> errors)
    {
        if (dto.Socks5Servers is null)
        {
            errors.Add(new("socks5Servers", "Field is required."));
            return;
        }

        for (var index = 0; index < dto.Socks5Servers.Count; index++)
        {
            ValidateServer(dto.Socks5Servers[index], index, servers, errors);
        }
    }

    private static void ValidateServer(Socks5ServerDto? dto, int index, Dictionary<string, Socks5Server> servers, List<ConfigDiagnostic> errors)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"socks5Servers[{index}]");
        if (dto is null)
        {
            errors.Add(new(path, "Server entry must be an object."));
            return;
        }

        var name = dto.Name?.Trim();
        if (string.IsNullOrEmpty(name)) errors.Add(new($"{path}.name", "Name is required."));
        else if (servers.ContainsKey(name)) errors.Add(new($"{path}.name", "Name must be unique (case-insensitive)."));

        var host = dto.Host?.Trim();
        if (string.IsNullOrEmpty(host) || !IsValidHost(host)) errors.Add(new($"{path}.host", "Host must be an IP literal or DNS hostname."));
        if (dto.Port is < 1 or > 65535) errors.Add(new($"{path}.port", "Port must be in 1..65535."));
        if ((dto.Username is null) != (dto.Password is null)) errors.Add(new(path, "username and password must be supplied together."));
        if (dto.Username is not null && (dto.Username.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(dto.Username) > 255)) errors.Add(new($"{path}.username", "Username must be 1..255 UTF-8 bytes."));
        if (dto.Password is not null && (dto.Password.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(dto.Password) > 255)) errors.Add(new($"{path}.password", "Password must be 1..255 UTF-8 bytes."));

        if (name is not null && host is not null && IsValidHost(host) && dto.Port is >= 1 and <= 65535 && !servers.ContainsKey(name))
        {
            servers.Add(name, new Socks5Server(name, host, (ushort)dto.Port, dto.Username, dto.Password));
        }
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

    private static bool IsValidHost(string host) => IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.Basic;
}
