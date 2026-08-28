using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinForward.Core;

namespace WinForward.Configuration;

public sealed class WinForwardConfigDto
{
    [JsonPropertyName("logLevel")]
    public JsonElement LogLevel { get; init; }

    [JsonPropertyName("socks5Servers")]
    public IReadOnlyList<Socks5ServerDto?>? Socks5Servers { get; init; }

    [JsonPropertyName("rules")]
    public IReadOnlyList<RuleDto?>? Rules { get; init; }

    [JsonPropertyName("fallbackAction")]
    public string? FallbackAction { get; init; }

    [JsonPropertyName("proxyUnavailableAction")]
    public string? ProxyUnavailableAction { get; init; }

    [JsonPropertyName("processingFailureAction")]
    public string? ProcessingFailureAction { get; init; }

    [JsonPropertyName("tcpFlowCapacity")]
    public int? TcpFlowCapacity { get; init; }
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

public sealed record ValidatedConfiguration(
    IReadOnlyDictionary<string, Socks5Server> Servers,
    PolicySnapshot Policy,
    RuntimeLogLevel LogLevel = RuntimeLogLevel.Info,
    bool IncludeProcessPathInLogs = false,
    int TcpFlowCapacity = ConfigurationLoader.DefaultTcpFlowCapacity)
{
    /// <summary>
    /// Non-blocking validation findings (for example a tcpFlowCapacity above the warning
    /// threshold) surfaced alongside an otherwise valid configuration.
    /// </summary>
    public IReadOnlyList<ConfigDiagnostic> Warnings { get; init; } = [];
}

public static class ConfigurationLoader
{
    /// <summary>The default concurrent proxied TCP flow budget: 16,384 ephemeral ports x 50% headroom / 2 ports per flow.</summary>
    public const int DefaultTcpFlowCapacity = 4_096;

    /// <summary>The smallest accepted tcpFlowCapacity; a zero or negative budget would block every flow.</summary>
    public const int MinimumTcpFlowCapacity = 1;

    /// <summary>The largest accepted tcpFlowCapacity; above this the budget offers no port-pool protection at all.</summary>
    public const int MaximumTcpFlowCapacity = 8_192;

    /// <summary>Values above the default warn during validation because they shrink the reserved ephemeral-port headroom.</summary>
    public const int TcpFlowCapacityWarningThreshold = 4_096;

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
        var tcpFlowCapacity = ParseTcpFlowCapacity(dto, errors, warnings);

        if (dto.Socks5Servers is null)
        {
            errors.Add(new("socks5Servers", "Field is required."));
        }
        else
        {
            for (var index = 0; index < dto.Socks5Servers.Count; index++)
            {
                ValidateServer(dto.Socks5Servers[index], index, servers, errors);
            }
        }

        var rules = new List<PolicyRule>();
        if (dto.Rules is null)
        {
            errors.Add(new("rules", "Field is required."));
        }
        else
        {
            for (var index = 0; index < dto.Rules.Count; index++)
            {
                var rule = ParseRule(dto.Rules[index], index, servers, errors);
                if (rule is not null) rules.Add(rule);
            }
        }

        var fallback = ParseAction(dto.FallbackAction, "fallbackAction", errors, allowProxy: false);
        ValidateFailureAction(dto.ProxyUnavailableAction, "proxyUnavailableAction", errors);
        ValidateFailureAction(dto.ProcessingFailureAction, "processingFailureAction", errors);

        if (errors.Count > 0 || fallback is null)
        {
            configuration = null;
            diagnostics = errors;
            return false;
        }

        configuration = new ValidatedConfiguration(
            servers,
            new PolicySnapshot(rules, fallback.Value),
            logLevel,
            rules.Any(static rule => rule.Matcher.Processes?.Any(IsPathSelector) == true),
            tcpFlowCapacity)
        {
            Warnings = warnings,
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

    /// <summary>
    /// Normalizes the optional tcpFlowCapacity budget. Omitted values fall back to the default;
    /// out-of-range values are rejected with the accepted range, and values above the default
    /// collect a non-blocking warning because they shrink the reserved ephemeral-port headroom.
    /// </summary>
    private static int ParseTcpFlowCapacity(WinForwardConfigDto dto, List<ConfigDiagnostic> errors, List<ConfigDiagnostic> warnings)
    {
        if (dto.TcpFlowCapacity is not { } value) return DefaultTcpFlowCapacity;
        if (value is < MinimumTcpFlowCapacity or > MaximumTcpFlowCapacity)
        {
            errors.Add(new("tcpFlowCapacity", $"TCP flow capacity must be in {MinimumTcpFlowCapacity}..{MaximumTcpFlowCapacity}."));
            return DefaultTcpFlowCapacity;
        }
        if (value > TcpFlowCapacityWarningThreshold)
        {
            warnings.Add(new("tcpFlowCapacity", $"Values above {TcpFlowCapacityWarningThreshold} leave less ephemeral-port headroom; each proxied TCP flow consumes 2 local ports."));
        }
        return value;
    }

    private static bool IsPathSelector(string selector) => selector.IndexOfAny(['/', '\\']) >= 0;

    private static void ValidateServer(Socks5ServerDto? dto, int index, Dictionary<string, Socks5Server> servers, List<ConfigDiagnostic> errors)
    {
        var path = $"socks5Servers[{index}]";
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

    private static PolicyRule? ParseRule(RuleDto? dto, int index, IReadOnlyDictionary<string, Socks5Server> servers, List<ConfigDiagnostic> errors)
    {
        var path = $"rules[{index}]";
        if (dto is null)
        {
            errors.Add(new(path, "Rule entry must be an object."));
            return null;
        }

        ValidateNonEmpty(dto.Process, $"{path}.process", errors);
        ValidateNonEmpty(dto.AdapterId, $"{path}.adapterId", errors);
        ValidateNonEmpty(dto.AdapterName, $"{path}.adapterName", errors);
        ValidateNonEmpty(dto.Protocol, $"{path}.protocol", errors);
        ValidateNonEmpty(dto.AddressFamily, $"{path}.addressFamily", errors);
        ValidateNonEmpty(dto.RemoteCidr, $"{path}.remoteCidr", errors);
        ValidateNonEmpty(dto.RemotePort, $"{path}.remotePort", errors);

        var action = ParseAction(dto.Action, $"{path}.action", errors, allowProxy: true);
        if (action is null) return null;
        var proxyServer = dto.ProxyServer?.Trim();
        if (action == FlowAction.Proxy && (string.IsNullOrWhiteSpace(proxyServer) || !servers.ContainsKey(proxyServer)))
        {
            errors.Add(new($"{path}.proxyServer", "Proxy action requires a configured proxy server."));
        }
        if (action != FlowAction.Proxy && dto.ProxyServer is not null) errors.Add(new($"{path}.proxyServer", "Only proxy rules may specify proxyServer."));

        var protocols = ParseSet(dto.Protocol, ParseProtocol, $"{path}.protocol", errors);
        var families = ParseSet(dto.AddressFamily, ParseFamily, $"{path}.addressFamily", errors);
        var networks = ParseNetworks(dto.RemoteCidr, $"{path}.remoteCidr", errors);
        var ports = ParsePorts(dto.RemotePort, $"{path}.remotePort", errors);
        if (errors.Any(error => error.Path.Equals(path, StringComparison.Ordinal) || error.Path.StartsWith(path + ".", StringComparison.Ordinal))) return null;

        return new PolicyRule(new RuleMatcher(
            NormalizeSet(dto.Process), NormalizeSet(dto.AdapterId), NormalizeSet(dto.AdapterName), protocols, families, networks, ports),
            new FlowDecision(action.Value, index, action == FlowAction.Proxy ? proxyServer : null));
    }

    private static FlowAction? ParseAction(string? raw, string path, List<ConfigDiagnostic> errors, bool allowProxy)
    {
        if (string.IsNullOrWhiteSpace(raw)) { errors.Add(new(path, "Action is required.")); return null; }
        var action = raw.Trim().ToLowerInvariant() switch
        {
            "proxy" when allowProxy => FlowAction.Proxy,
            "pass" => FlowAction.Pass,
            "block" => FlowAction.Block,
            _ => (FlowAction?)null
        };
        if (action is null)
        {
            errors.Add(new(path, allowProxy ? "Action must be proxy, pass, or block." : "Action must be pass or block."));
            return null;
        }
        return action.Value;
    }

    private static void ValidateFailureAction(string? raw, string path, List<ConfigDiagnostic> errors)
    {
        if (raw is not null && !string.Equals(raw.Trim(), "block", StringComparison.OrdinalIgnoreCase)) errors.Add(new(path, "Only block is supported in the first release."));
    }

    private static void ValidateNonEmpty(string?[]? values, string path, List<ConfigDiagnostic> errors)
    {
        if (values is null) return;
        if (values.Length == 0)
        {
            errors.Add(new(path, "Array must not be empty."));
            return;
        }

        for (var index = 0; index < values.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(values[index])) errors.Add(new($"{path}[{index}]", "Value must not be empty."));
        }
    }

    private static IReadOnlySet<string>? NormalizeSet(string?[]? values) => values is null ? null : values.Select(static value => value!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<IPPrefix>? ParseNetworks(string?[]? values, string path, List<ConfigDiagnostic> errors)
    {
        if (values is null) return null;
        var result = new List<IPPrefix>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!IPPrefix.TryParse(value, out var prefix)) errors.Add(new(path, $"Invalid CIDR '{value}'.")); else result.Add(prefix);
        }
        return result;
    }

    private static HashSet<T>? ParseSet<T>(string?[]? values, Func<string, T?> parser, string path, List<ConfigDiagnostic> errors) where T : struct
    {
        if (values is null) return null;
        var result = new HashSet<T>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var parsed = parser(value);
            if (parsed is null) errors.Add(new(path, $"Unsupported value '{value}'.")); else result.Add(parsed.Value);
        }
        return result;
    }

    private static IReadOnlyList<(ushort Start, ushort End)>? ParsePorts(string?[]? values, string path, List<ConfigDiagnostic> errors)
    {
        if (values is null) return null;
        var result = new List<(ushort Start, ushort End)>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var parts = value.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 2 || !ushort.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var start) || start == 0)
            {
                errors.Add(new(path, $"Invalid port or range '{value}'."));
                continue;
            }

            var end = start;
            if (parts.Length == 2 && (!ushort.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out end) || end == 0 || end < start))
            {
                errors.Add(new(path, $"Invalid port or range '{value}'."));
                continue;
            }

            result.Add((start, end));
        }

        result.Sort(static (left, right) => left.Start != right.Start
            ? left.Start.CompareTo(right.Start)
            : left.End.CompareTo(right.End));
        var merged = new List<(ushort Start, ushort End)>(result.Count);
        foreach (var range in result)
        {
            if (merged.Count == 0)
            {
                merged.Add(range);
                continue;
            }

            var previous = merged[^1];
            if ((uint)range.Start > (uint)previous.End + 1U)
            {
                merged.Add(range);
                continue;
            }

            if (range.End > previous.End) merged[^1] = (previous.Start, range.End);
        }

        return merged;
    }

    private static TransportProtocol? ParseProtocol(string value)
    {
        if (value.Equals("tcp", StringComparison.OrdinalIgnoreCase)) return TransportProtocol.Tcp;
        if (value.Equals("udp", StringComparison.OrdinalIgnoreCase)) return TransportProtocol.Udp;
        return null;
    }
    private static AddressFamilyKind? ParseFamily(string value)
    {
        if (value.Equals("IPv4", StringComparison.OrdinalIgnoreCase) || value.Equals("InterNetwork", StringComparison.OrdinalIgnoreCase)) return AddressFamilyKind.IPv4;
        if (value.Equals("IPv6", StringComparison.OrdinalIgnoreCase) || value.Equals("InterNetworkV6", StringComparison.OrdinalIgnoreCase)) return AddressFamilyKind.IPv6;
        return null;
    }

    private static bool IsValidHost(string host) => IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.Basic;
}
