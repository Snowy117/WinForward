using System.Text.Json;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Configuration;

/// <summary>
/// host 规则选择器的数组解析：每个字段同时接受单值与数组，单值等价于单元素数组。
/// </summary>
public static partial class PolicyConfigurationDocument
{
    private static ProcessSelector[]? ParseProcessList(JsonElement parent, string path)
    {
        IReadOnlyList<ConfigText>? values = ReadStringList(parent, path);
        if (values is null)
        {
            return null;
        }

        List<ProcessSelector> selectors = new(values.Count);
        foreach (ConfigText value in values)
        {
            try
            {
                selectors.Add(ProcessSelector.Parse(value.Value));
            }
            catch (ArgumentException exception)
            {
                throw new ConfigurationException(
                    $"{value.Path} 不是可写进规则池的路径：「{value.Value}」（{exception.Message}）", exception);
            }
        }

        return selectors.ToArray();
    }

    private static RemoteNetwork[]? ParseNetworkList(JsonElement parent, string path)
    {
        IReadOnlyList<ConfigText>? values = ReadStringList(parent, path);
        if (values is null)
        {
            return null;
        }

        List<RemoteNetwork> networks = new(values.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ConfigText value in values)
        {
            RemoteNetwork network;
            try
            {
                network = RemoteNetwork.Parse(value.Value);
            }
            catch (ArgumentException exception)
            {
                throw new ConfigurationException(
                    $"{value.Path} 不是合法 CIDR（address/prefix）：「{value.Value}」", exception);
            }

            if (seen.Add(network.ToString()))
            {
                networks.Add(network);
            }
        }

        return networks.ToArray();
    }

    private static PortRange[]? ParsePortList(JsonElement parent, string path)
    {
        IReadOnlyList<ConfigText>? values = ReadStringList(parent, path);
        if (values is null)
        {
            return null;
        }

        if (values.Count == 1 && values[0].Value == "*")
        {
            return null;
        }

        List<PortRange> ranges = new(values.Count);
        foreach (ConfigText value in values)
        {
            if (value.Value == "*")
            {
                throw new ConfigurationException(
                    $"{value.Path}：「*」只能单独出现，表示不限制远端端口");
            }

            ranges.Add(ParseHostPortRange(value));
        }

        ranges.Sort(static (left, right) =>
                        left.Begin != right.Begin ? left.Begin.CompareTo(right.Begin) : left.End.CompareTo(right.End));

        List<PortRange> merged = new(ranges.Count);
        foreach (PortRange range in ranges)
        {
            if (merged.Count > 0 && range.Begin <= merged[^1].End + 1)
            {
                PortRange last = merged[^1];
                merged[^1] = new PortRange(last.Begin, (ushort)Math.Max(last.End, range.End));
                continue;
            }

            merged.Add(range);
        }

        return merged.ToArray();
    }

    private static RuleProtocol[]? ParseProtocolList(JsonElement parent, string path)
    {
        IReadOnlyList<ConfigText>? values = ReadStringList(parent, path);
        if (values is null)
        {
            return null;
        }

        List<RuleProtocol> protocols = new(values.Count);
        bool any = false;
        foreach (ConfigText value in values)
        {
            RuleProtocol protocol = value.Value.ToLowerInvariant() switch
            {
                "any" => RuleProtocol.Any,
                "tcp" => RuleProtocol.Tcp,
                "udp" => RuleProtocol.Udp,
                _ => throw new ConfigurationException(
                    $"{value.Path} 只能是 any / tcp / udp，实际是「{value.Value}」"),
            };
            any |= protocol == RuleProtocol.Any;
            protocols.Add(protocol);
        }

        if (any)
        {
            return null;
        }

        return protocols.Distinct().ToArray();
    }

    private static RuleAddressFamily[]? ParseFamilyList(JsonElement parent, string path)
    {
        IReadOnlyList<ConfigText>? values = ReadStringList(parent, path);
        if (values is null)
        {
            return null;
        }

        List<RuleAddressFamily> families = new(values.Count);
        bool any = false;
        foreach (ConfigText value in values)
        {
            RuleAddressFamily family = value.Value.ToLowerInvariant() switch
            {
                "any" => RuleAddressFamily.Any,
                "ipv4" or "v4" or "internetwork" => RuleAddressFamily.Ipv4,
                "ipv6" or "v6" or "internetworkv6" => RuleAddressFamily.Ipv6,
                _ => throw new ConfigurationException(
                    $"{value.Path} 只能是 any / ipv4 / ipv6，实际是「{value.Value}」"),
            };
            any |= family == RuleAddressFamily.Any;
            families.Add(family);
        }

        if (any)
        {
            return null;
        }

        return families.Distinct().ToArray();
    }

    private static PortRange ParseHostPortRange(ConfigText text)
    {
        int dash = text.Value.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return PortRange.Single(ParseHostPort(text.Value, text.Path));
        }

        ushort begin = ParseHostPort(text.Value[..dash].Trim(), text.Path);
        ushort end = ParseHostPort(text.Value[(dash + 1)..].Trim(), text.Path);
        if (begin > end)
        {
            throw new ConfigurationException($"{text.Path} 的端口区间颠倒：{begin}-{end}");
        }

        return new PortRange(begin, end);
    }

    private static ushort ParseHostPort(string text, string path)
    {
        if (!ushort.TryParse(text, System.Globalization.NumberStyles.None,
                             System.Globalization.CultureInfo.InvariantCulture, out ushort port))
        {
            throw new ConfigurationException($"{path} 的端口不是 0..65535 的十进制整数：「{text}」");
        }

        if (port == 0)
        {
            throw new ConfigurationException($"{path} 不允许端口 0；不限制时请写「*」或省略该字段");
        }

        return port;
    }

    private static IReadOnlyList<ConfigText>? ReadStringList(JsonElement parent, string path)
    {
        int dot = path.LastIndexOf('.');
        string name = dot < 0 ? path : path[(dot + 1)..];
        string owner = dot < 0 ? string.Empty : path[..dot];
        if (!TryGetProperty(parent, name, owner, required: false, out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return new[] { ReadElement(element, path) };
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ConfigurationException($"{path} 必须是字符串或字符串数组，实际是 {element.ValueKind}");
        }

        List<ConfigText> values = new();
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            values.Add(ReadElement(item, $"{path}[{index}]"));
            index++;
        }

        if (values.Count == 0)
        {
            throw new ConfigurationException($"{path} 不能是空数组；不限制时请省略该字段");
        }

        return values;
    }

    private static ConfigText ReadElement(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new ConfigurationException($"{path} 必须是字符串，实际是 {element.ValueKind}");
        }

        string value = (element.GetString() ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new ConfigurationException($"{path} 不能为空或纯空白");
        }

        return new ConfigText(value, path);
    }

    private readonly struct ConfigText
    {
        internal ConfigText(string value, string path)
        {
            Value = value;
            Path = path;
        }

        internal string Value { get; }

        internal string Path { get; }
    }
}
