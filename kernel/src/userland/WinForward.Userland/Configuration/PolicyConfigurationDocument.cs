using System.Text.Json;
using WinForward.Userland.Forwarded;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Configuration;

/// <summary>
/// 配置文件（暂定 JSON）与 <see cref="PolicyTable"/> / <see cref="ControllerConfiguration"/> 之间唯一的转换点。
/// </summary>
/// <remarks>
/// 配置格式是暂定的：换格式（YAML、TOML、注册表、命令行）时只改这个类型（主文件与
/// <c>.Forwarded.cs</c> 分部）。<c>Relay/</c> 只认 <see cref="PolicyTable"/>，<c>Control/</c> 还接收
/// forwarded 表与上游表，不感知序列化形式。
///
/// host 规则在配置层做「结构 + 取值」校验（字段名、类型、数组形状、端口语法/CIDR 语法），
/// 跨字段语义（族与网段一致性、规则顺序冲突、block 表达边界、进程路径解析与长度）留在
/// <see cref="RuleBlobCompiler"/>，错误消息统一带规则序号；forwarded 规则
/// 在配置层提前校验 target / adapter / CIDR / 端口，<see cref="ForwardedBlobCompiler"/> 再做最终复核。
/// 数组字段的约定：同一字段内是 OR、字段之间是 AND；<c>protocol</c> / <c>family</c> 出现
/// <c>any</c> 时视为不限制（与具体值混写时 <c>any</c> 优先），<c>remotePorts</c> 的 <c>*</c>
/// 只能单独出现。
///
/// 最小 schema（旧字段保持不变，新增 <c>socks5Servers</c> 与 <c>forwarded</c>）：
/// <code>
/// {
///   "fallbackAction": "permit" | "block" | "redirect",
///   "socks5": { "host": "127.0.0.1", "port": 1080 },   // 可选；存在时隐式成为名为 default 的上游
///   "socks5Servers": [                                  // 可选；名字非空且唯一（大小写不敏感）
///     { "name": "main", "host": "proxy.example.com", "port": 1080 }
///   ],
///   "rules": [
///     {
///       "name": "可选的诊断名",
///       "action": "permit" | "block" | "redirect",
///       "protocol": "any" | "tcp" | "udp" | ["tcp", "udp"],       // 单值或数组
///       "family": "any" | "ipv4" | "ipv6" | ["ipv4", "ipv6"],      // 单值或数组
///       "remotePorts": "*" | "443" | "8000-8100" | ["80", "443"],  // 单值或数组
///       "remoteNetwork": "10.0.0.0/8" | ["10.0.0.0/8", "fc00::/7"],// 单值或数组
///       "process": "C:\\Tools\\app.exe" | ["browser.exe", "C:\\Tools\\curl.exe"]
///     }
///   ],
///   "forwarded": {                                      // 可选；不写就是没有 forwarded 规则
///     "fallbackAction": "permit" | "block",             // 默认 permit
///     "rules": [
///       {
///         "name": "可选的诊断名",
///         "adapterId": "稳定 id",                        // 可单独给；与 adapterName 同时给时必须一致
///         "adapterName": "vEthernet (MyVM)",
///         "clientCidr": "10.0.0.0/24",
///         "remoteCidr": "203.0.113.0/24",
///         "remotePorts": "*" | "443" | "8000-8100",
///         "protocol": "any" | "tcp" | "udp",
///         "family": "any" | "ipv4" | "ipv6",
///         "action": "permit" | "block" | "redirect",
///         "target": "main"                              // redirect 必填；非 redirect 禁止出现
///       }
///     ]
///   }
/// }
/// </code>
///
/// forwarded 规则没有宿主进程，出现 <c>process</c> 直接报错；带 adapter 选择器的规则
/// 需要调用方提供 <see cref="IAdapterIdResolver"/>，配置层不会猜编号。
/// </remarks>
public static partial class PolicyConfigurationDocument
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    public static PolicyTable Load(string path, IAdapterIdResolver? adapterResolver = null) =>
        LoadConfiguration(path, adapterResolver).Policy;

    public static ControllerConfiguration LoadConfiguration(string path,
                                                               IAdapterIdResolver? adapterResolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationException($"读取配置文件失败：{path}（{exception.Message}）", exception);
        }

        return ParseConfiguration(json, adapterResolver);
    }

    public static PolicyTable Parse(string json, IAdapterIdResolver? adapterResolver = null) =>
        ParseConfiguration(json, adapterResolver).Policy;

    public static ControllerConfiguration ParseConfiguration(string json,
                                                               IAdapterIdResolver? adapterResolver = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException exception)
        {
            throw new ConfigurationException($"JSON 语法错误：{exception.Message}", exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigurationException($"配置根节点必须是对象，实际是 {root.ValueKind}");
            }
            RejectUnknownProperties(root, string.Empty,
                                    "fallbackAction", "rules", "socks5", "socks5Servers", "forwarded");

            RuleAction fallbackAction = ParseAction(root, "fallbackAction", required: true);
            Socks5Endpoint? socks5 = ParseSocks5(root);
            List<PolicyRule> rules = new();

            if (TryGetProperty(root, "rules", "根节点", required: false, out JsonElement rulesElement))
            {
                if (rulesElement.ValueKind != JsonValueKind.Array)
                {
                    throw new ConfigurationException($"rules 必须是数组，实际是 {rulesElement.ValueKind}");
                }

                int index = 0;
                foreach (JsonElement element in rulesElement.EnumerateArray())
                {
                    rules.Add(ParseRule(element, $"rules[{index}]"));
                    index++;
                }
            }

            List<ForwardedUpstream> forwardedUpstreams = new();
            ParseSocks5Servers(root, forwardedUpstreams);

            if (socks5 is not null)
            {
                if (forwardedUpstreams.Any(upstream =>
                        string.Equals(upstream.Name, "default", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ConfigurationException(
                        "socks5 段与 socks5Servers 冲突：旧 socks5 隐式占用名为 default 的上游");
                }

                forwardedUpstreams.Add(new ForwardedUpstream((uint)forwardedUpstreams.Count, "default", socks5));
            }

            Dictionary<string, Socks5Endpoint> upstreams = new(StringComparer.OrdinalIgnoreCase);
            foreach (ForwardedUpstream upstream in forwardedUpstreams)
            {
                upstreams.Add(upstream.Name, upstream.Endpoint);
            }

            ForwardedPolicyTable? forwarded = ParseForwarded(root, forwardedUpstreams, upstreams, adapterResolver);

            return new ControllerConfiguration
            {
                Policy = new PolicyTable(rules, fallbackAction),
                Socks5 = socks5,
                Forwarded = forwarded,
                Upstreams = upstreams,
                ForwardedUpstreams = forwardedUpstreams,
            };
        }
    }

    private static Socks5Endpoint? ParseSocks5(JsonElement root)
    {
        if (!TryGetProperty(root, "socks5", "根节点", required: false, out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigurationException($"socks5 必须是对象，实际是 {element.ValueKind}");
        }

        RejectUnknownProperties(element, "socks5", "host", "port");
        return ParseSocks5Endpoint(element, "socks5");
    }

    private static Socks5Endpoint ParseSocks5Endpoint(JsonElement element, string path)
    {
        string host = ReadString(element, path + ".host", required: true) ?? string.Empty;
        _ = TryGetProperty(element, "port", path, required: true, out JsonElement portElement);
        if (portElement.ValueKind != JsonValueKind.Number || !portElement.TryGetInt32(out int port))
        {
            throw new ConfigurationException($"{path}.port 必须是 1..65535 的十进制整数，实际是 {portElement.ValueKind}");
        }

        if (port is < 1 or > 65535)
        {
            throw new ConfigurationException($"{path}.port 必须在 1..65535，实际是 {port}");
        }

        return new Socks5Endpoint(host, port);
    }

    private static PolicyRule ParseRule(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigurationException($"{path} 必须是对象，实际是 {element.ValueKind}");
        }

        RejectUnknownProperties(element, path, "name", "action", "protocol", "family", "remotePorts", "remoteNetwork", "process");
        string? name = ReadOptionalString(element, path, "name");
        RuleAction action = ParseAction(element, path + ".action", required: true);

        return new PolicyRule
        {
            Name = name,
            Action = action,
            Protocols = ParseProtocolList(element, path + ".protocol"),
            Families = ParseFamilyList(element, path + ".family"),
            RemotePorts = ParsePortList(element, path + ".remotePorts"),
            RemoteNetworks = ParseNetworkList(element, path + ".remoteNetwork"),
            Processes = ParseProcessList(element, path + ".process"),
        };
    }

    private static RuleAction ParseAction(JsonElement parent, string path, bool required)
    {
        string? text = ReadString(parent, path, required);
        if (text is null)
        {
            return RuleAction.Permit;
        }

        return text.ToLowerInvariant() switch
        {
            "permit" => RuleAction.Permit,
            "block" => RuleAction.Block,
            "redirect" => RuleAction.Redirect,
            _ => throw new ConfigurationException($"{path} 只能是 permit / block / redirect，实际是「{text}」"),
        };
    }

    private static RuleProtocol ParseProtocol(JsonElement parent, string path)
    {
        string? text = ReadString(parent, path, required: false);
        return text?.ToLowerInvariant() switch
        {
            null or "any" => RuleProtocol.Any,
            "tcp" => RuleProtocol.Tcp,
            "udp" => RuleProtocol.Udp,
            _ => throw new ConfigurationException($"{path} 只能是 any / tcp / udp，实际是「{text}」"),
        };
    }

    private static RuleAddressFamily ParseFamily(JsonElement parent, string path)
    {
        string? text = ReadString(parent, path, required: false);
        return text?.ToLowerInvariant() switch
        {
            null or "any" => RuleAddressFamily.Any,
            "ipv4" or "v4" => RuleAddressFamily.Ipv4,
            "ipv6" or "v6" => RuleAddressFamily.Ipv6,
            _ => throw new ConfigurationException($"{path} 只能是 any / ipv4 / ipv6，实际是「{text}」"),
        };
    }

    private static PortRange ParsePorts(JsonElement parent, string path)
    {
        string? text = ReadString(parent, path, required: false);
        if (text is null || text == "*")
        {
            return PortRange.Any;
        }

        int dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            ushort port = ParsePort(text, path);
            return PortRange.Single(port);
        }

        ushort begin = ParsePort(text[..dash].Trim(), path);
        ushort end = ParsePort(text[(dash + 1)..].Trim(), path);
        return new PortRange(begin, end);

    }

    private static ushort ParsePort(string text, string path)
    {
        if (!ushort.TryParse(text, System.Globalization.NumberStyles.None,
                             System.Globalization.CultureInfo.InvariantCulture, out ushort port))
        {
            throw new ConfigurationException($"{path} 的端口不是 0..65535 的十进制整数：「{text}」");
        }

        return port;
    }

    private static RemoteNetwork? ParseNetwork(JsonElement parent, string path)
    {
        string? text = ReadString(parent, path, required: false);
        if (text is null)
        {
            return null;
        }

        try
        {
            return RemoteNetwork.Parse(text);
        }
        catch (ArgumentException exception)
        {
            throw new ConfigurationException($"{path} 不是合法 CIDR（address/prefix）：「{text}」", exception);
        }
    }

    private static string? ReadOptionalString(JsonElement parent, string path, string name) =>
        ReadString(parent, $"{path}.{name}", required: false);

    private static string? ReadString(JsonElement parent, string path, bool required)
    {
        string name = path[(path.LastIndexOf('.') + 1)..];
        if (!TryGetProperty(parent, name, path[..Math.Max(0, path.LastIndexOf('.') + 1)].TrimEnd('.'), required, out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new ConfigurationException($"{path} 必须是字符串，实际是 {element.ValueKind}");
        }

        string value = element.GetString() ?? string.Empty;
        if (required && string.IsNullOrWhiteSpace(value))
        {
            throw new ConfigurationException($"{path} 不能为空");
        }

        return value;
    }

    private static bool TryGetProperty(JsonElement parent,
                                       string name,
                                       string owner,
                                       bool required,
                                       out JsonElement value)
    {
        foreach (JsonProperty property in parent.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            value = property.Value;
            return true;
        }

        if (required)
        {
            string full = owner.Length == 0 ? name : $"{owner}.{name}";
            throw new ConfigurationException($"{full} 是必填字段");
        }

        value = default;
        return false;
    }
    private static void RejectUnknownProperties(JsonElement element, string owner, params string[] allowed)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }

            string path = owner.Length == 0 ? property.Name : $"{owner}.{property.Name}";
            throw new ConfigurationException($"{path} 不是已知字段；可用字段：{string.Join(", ", allowed)}");
        }
    }
}
