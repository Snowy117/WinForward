using System.Text.Json;
using WinForward.Userland.Forwarded;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Configuration;

/// <summary>
/// <see cref="PolicyConfigurationDocument"/> 的 forwarded / 多上游解析半部分。
/// </summary>
/// <remarks>
/// 诊断路径统一是 <c>forwarded.rules[i].&lt;field&gt;</c>。
/// </remarks>
public static partial class PolicyConfigurationDocument
{
    private static void ParseSocks5Servers(JsonElement root, List<ForwardedUpstream> upstreams)
    {
        if (!TryGetProperty(root, "socks5Servers", "根节点", required: false, out JsonElement element))
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ConfigurationException($"socks5Servers 必须是数组，实际是 {element.ValueKind}");
        }

        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string path = $"socks5Servers[{index}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigurationException($"{path} 必须是对象，实际是 {item.ValueKind}");
            }

            RejectUnknownProperties(item, path, "name", "host", "port");
            string name = (ReadString(item, path + ".name", required: true) ?? string.Empty).Trim();
            if (upstreams.Any(upstream =>
                    string.Equals(upstream.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConfigurationException(
                    $"{path}.name 与之前的上游重名：「{name}」（名字大小写不敏感）");
            }

            Socks5Endpoint endpoint = ParseSocks5Endpoint(item, path);
            upstreams.Add(new ForwardedUpstream((uint)upstreams.Count, name, endpoint));
            index++;
        }
    }

    private static ForwardedPolicyTable? ParseForwarded(
        JsonElement root,
        IReadOnlyList<ForwardedUpstream> forwardedUpstreams,
        IReadOnlyDictionary<string, Socks5Endpoint> upstreams,
        IAdapterIdResolver? adapterResolver)
    {
        if (!TryGetProperty(root, "forwarded", "根节点", required: false, out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigurationException($"forwarded 必须是对象，实际是 {element.ValueKind}");
        }

        RejectUnknownProperties(element, "forwarded", "fallbackAction", "rules");
        RuleAction fallbackAction = ParseForwardedFallbackAction(element);
        List<ForwardedPolicyRule> rules = new();

        if (TryGetProperty(element, "rules", "forwarded", required: false, out JsonElement rulesElement))
        {
            if (rulesElement.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigurationException($"forwarded.rules 必须是数组，实际是 {rulesElement.ValueKind}");
            }

            int index = 0;
            foreach (JsonElement ruleElement in rulesElement.EnumerateArray())
            {
                rules.Add(ParseForwardedRule(ruleElement,
                                             $"forwarded.rules[{index}]",
                                             forwardedUpstreams,
                                             upstreams,
                                             adapterResolver));
                index++;
            }
        }

        return new ForwardedPolicyTable(rules, fallbackAction);
    }

    private static RuleAction ParseForwardedFallbackAction(JsonElement forwarded)
    {
        string? text = ReadString(forwarded, "forwarded.fallbackAction", required: false);
        return text?.ToLowerInvariant() switch
        {
            null or "permit" => RuleAction.Permit,
            "block" => RuleAction.Block,
            "redirect" => throw new ConfigurationException(
                "forwarded.fallbackAction 只能是 permit / block（forwarded 兜底不支持 redirect）"),
            _ => throw new ConfigurationException(
                $"forwarded.fallbackAction 只能是 permit / block，实际是「{text}」"),
        };
    }

    private static ForwardedPolicyRule ParseForwardedRule(
        JsonElement element,
        string path,
        IReadOnlyList<ForwardedUpstream> forwardedUpstreams,
        IReadOnlyDictionary<string, Socks5Endpoint> upstreams,
        IAdapterIdResolver? adapterResolver)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigurationException($"{path} 必须是对象，实际是 {element.ValueKind}");
        }

        string? name = ReadOptionalString(element, path, "name");
        if (HasProperty(element, "process"))
        {
            throw new ConfigurationException(
                $"{path}.process 不允许出现在 forwarded 规则里：forwarded 没有宿主进程{RuleNote(name)}");
        }

        RejectUnknownProperties(element, path,
                                "name", "adapterId", "adapterName", "clientCidr", "remoteCidr",
                                "remotePorts", "protocol", "family", "action", "target");

        RuleAction action = ParseAction(element, path + ".action", required: true);
        RuleProtocol protocol = ParseProtocol(element, path + ".protocol");
        RuleAddressFamily family = ParseFamily(element, path + ".family");
        PortRange ports = ParsePorts(element, path + ".remotePorts");
        if (ports.Begin > ports.End)
        {
            throw new ConfigurationException($"{path}.remotePorts 的区间颠倒：{ports}");
        }

        RemoteNetwork? clientNetwork = ParseNetwork(element, path + ".clientCidr");
        RemoteNetwork? remoteNetwork = ParseNetwork(element, path + ".remoteCidr");
        ValidateForwardedNetworkFamily(path, family, "clientCidr", clientNetwork);
        ValidateForwardedNetworkFamily(path, family, "remoteCidr", remoteNetwork);
        if (clientNetwork is not null && remoteNetwork is not null && clientNetwork.Family != remoteNetwork.Family)
        {
            throw new ConfigurationException(
                $"{path} 的 clientCidr 族 {(byte)clientNetwork.Family} 与 remoteCidr 族 " +
                $"{(byte)remoteNetwork.Family} 不同，内核会整块拒绝");
        }

        uint? ingressAdapterId = ResolveAdapter(element, path, name, adapterResolver);
        uint? upstreamId = ResolveUpstream(element, path, name, action, forwardedUpstreams, upstreams);

        return new ForwardedPolicyRule
        {
            Name = name,
            IngressAdapterId = ingressAdapterId,
            ClientNetwork = clientNetwork,
            RemoteNetwork = remoteNetwork,
            Family = family,
            Protocol = protocol,
            RemotePorts = ports,
            Action = action,
            UpstreamId = upstreamId,
        };
    }

    private static void ValidateForwardedNetworkFamily(
        string path,
        RuleAddressFamily family,
        string field,
        RemoteNetwork? network)
    {
        if (network is null || family == RuleAddressFamily.Any || family == network.Family)
        {
            return;
        }

        throw new ConfigurationException(
            $"{path}.{field} 的网段族 {(byte)network.Family} 与 family {(byte)family} 不一致");
    }

    private static uint? ResolveAdapter(
        JsonElement element,
        string path,
        string? name,
        IAdapterIdResolver? adapterResolver)
    {
        string? adapterId = NormalizeAdapterSelector(
            ReadOptionalString(element, path, "adapterId"), path + ".adapterId");
        string? adapterName = NormalizeAdapterSelector(
            ReadOptionalString(element, path, "adapterName"), path + ".adapterName");
        if (adapterId is null && adapterName is null)
        {
            return null;
        }

        string note = RuleNote(name);
        if (adapterResolver is null)
        {
            throw new ConfigurationException(
                $"{path} 带 adapterId/adapterName，但配置解析没有提供 IAdapterIdResolver；没有 resolver 时不能猜适配器编号{note}");
        }

        uint? idFromId = null;
        uint? idFromName = null;
        if (adapterId is not null)
        {
            if (!adapterResolver.TryResolve(adapterId, null, out uint id, out string error))
            {
                throw new ConfigurationException($"{path}.adapterId 解析失败：「{adapterId}」（{error}）{note}");
            }

            idFromId = id;
        }

        if (adapterName is not null)
        {
            if (!adapterResolver.TryResolve(null, adapterName, out uint id, out string error))
            {
                throw new ConfigurationException($"{path}.adapterName 解析失败：「{adapterName}」（{error}）{note}");
            }

            idFromName = id;
        }

        if (idFromId is not null && idFromName is not null && idFromId != idFromName)
        {
            throw new ConfigurationException(
                $"{path}.adapterId 与 adapterName 解析结果不一致：{idFromId} != {idFromName}{note}");
        }

        uint resolved = idFromId ?? idFromName!.Value;
        if (resolved == ForwardedBlobLayout.AdapterAny)
        {
            throw new ConfigurationException(
                $"{path} 的适配器解析结果是 0xFFFFFFFF（任意入口哨兵），不能用作具体入口{note}");
        }

        return resolved;
    }

    private static uint? ResolveUpstream(
        JsonElement element,
        string path,
        string? name,
        RuleAction action,
        IReadOnlyList<ForwardedUpstream> forwardedUpstreams,
        IReadOnlyDictionary<string, Socks5Endpoint> upstreams)
    {
        bool hasTarget = TryGetProperty(element, "target", path, required: false, out JsonElement targetElement);
        if (action != RuleAction.Redirect)
        {
            if (hasTarget)
            {
                throw new ConfigurationException(
                    $"{path}.target 只能出现在 redirect 规则上（当前动作 {action.ToString().ToLowerInvariant()}）{RuleNote(name)}");
            }

            return null;
        }

        if (!hasTarget)
        {
            throw new ConfigurationException($"{path}.target 是 redirect 规则的必填字段{RuleNote(name)}");
        }

        if (targetElement.ValueKind != JsonValueKind.String)
        {
            throw new ConfigurationException($"{path}.target 必须是字符串，实际是 {targetElement.ValueKind}{RuleNote(name)}");
        }

        string target = (targetElement.GetString() ?? string.Empty).Trim();
        if (target.Length == 0)
        {
            throw new ConfigurationException($"{path}.target 不能为空{RuleNote(name)}");
        }

        if (!upstreams.ContainsKey(target))
        {
            throw new ConfigurationException(
                $"{path}.target 引用了不存在的上游「{target}」；可用上游：{DescribeUpstreams(forwardedUpstreams)}{RuleNote(name)}");
        }

        foreach (ForwardedUpstream upstream in forwardedUpstreams)
        {
            if (string.Equals(upstream.Name, target, StringComparison.OrdinalIgnoreCase))
            {
                return upstream.Id;
            }
        }

        throw new ConfigurationException(
            $"{path}.target 引用了上游「{target}」，但有序上游表里没有这一项{RuleNote(name)}");
    }

    private static string? NormalizeAdapterSelector(string? value, string path)
    {
        if (value is null)
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new ConfigurationException($"{path} 不能为空");
        }

        return trimmed;
    }

    private static string RuleNote(string? name) =>
        string.IsNullOrEmpty(name) ? string.Empty : $"（规则名：{name}）";

    private static string DescribeUpstreams(IReadOnlyList<ForwardedUpstream> upstreams) =>
        upstreams.Count == 0
            ? "（没有配置任何上游）"
            : string.Join(", ", upstreams.Select(upstream => upstream.Name));

    private static bool HasProperty(JsonElement element, string name)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
