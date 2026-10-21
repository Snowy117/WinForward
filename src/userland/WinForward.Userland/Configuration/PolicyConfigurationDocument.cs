using System.Text.Json;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Configuration;

/// <summary>
/// 配置文件（暂定 JSON）与 <see cref="PolicyTable"/> 之间唯一的转换点。
/// </summary>
/// <remarks>
/// 配置格式是暂定的：换格式（YAML、TOML、注册表、命令行）时只改这个文件，
/// <c>Control/</c> 与 <c>Relay/</c> 只认 <see cref="PolicyTable"/>，不感知序列化形式。
///
/// 这里只做「结构」校验（字段名、类型、字符串语法）。语义校验（端口区间颠倒、规则顺序冲突、
/// 进程路径超长等）留在 <see cref="RuleBlobCompiler"/>，错误消息统一带规则序号。
///
/// 最小 schema：
/// <code>
/// {
///   "fallbackAction": "permit" | "block" | "redirect",
///   "socks5": { "host": "127.0.0.1", "port": 1080 },   // 可选；不写就是直连
///   "rules": [
///     {
///       "name": "可选的诊断名",
///       "action": "permit" | "block" | "redirect",
///       "protocol": "any" | "tcp",
///       "family": "any" | "ipv4" | "ipv6",
///       "remotePorts": "*" | "443" | "8000-8100",
///       "remoteNetwork": "10.0.0.0/8",
///       "process": "C:\\Tools\\app.exe"
///     }
///   ]
/// }
/// </code>
/// </remarks>
public static class PolicyConfigurationDocument
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    public static PolicyTable Load(string path) => LoadConfiguration(path).Policy;

    public static ControllerConfiguration LoadConfiguration(string path)
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

        return ParseConfiguration(json);
    }

    public static PolicyTable Parse(string json) => ParseConfiguration(json).Policy;

    public static ControllerConfiguration ParseConfiguration(string json)
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
            RejectUnknownProperties(root, string.Empty, "fallbackAction", "rules", "socks5");

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

            return new ControllerConfiguration { Policy = new PolicyTable(rules, fallbackAction), Socks5 = socks5 };
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
        string host = ReadString(element, "socks5.host", required: true) ?? string.Empty;
        _ = TryGetProperty(element, "port", "socks5", required: true, out JsonElement portElement);
        if (portElement.ValueKind != JsonValueKind.Number || !portElement.TryGetInt32(out int port))
        {
            throw new ConfigurationException($"socks5.port 必须是 1..65535 的十进制整数，实际是 {portElement.ValueKind}");
        }

        if (port is < 1 or > 65535)
        {
            throw new ConfigurationException($"socks5.port 必须在 1..65535，实际是 {port}");
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
        RuleProtocol protocol = ParseProtocol(element, path + ".protocol");
        RuleAddressFamily family = ParseFamily(element, path + ".family");
        PortRange ports = ParsePorts(element, path + ".remotePorts");
        RemoteNetwork? network = ParseNetwork(element, path + ".remoteNetwork");
        ProcessSelector? process = ParseProcess(element, path + ".process");

        return new PolicyRule
        {
            Name = name,
            Action = action,
            Protocol = protocol,
            Family = family,
            RemotePorts = ports,
            RemoteNetwork = network,
            Process = process,
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
            _ => throw new ConfigurationException($"{path} 只能是 any / tcp，实际是「{text}」"),
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

    private static ProcessSelector? ParseProcess(JsonElement parent, string path)
    {
        string? text = ReadString(parent, path, required: false);
        if (text is null)
        {
            return null;
        }

        try
        {
            return ProcessSelector.Parse(text);
        }
        catch (ArgumentException exception)
        {
            throw new ConfigurationException($"{path} 不是可写进规则池的路径：「{text}」（{exception.Message}）", exception);
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

