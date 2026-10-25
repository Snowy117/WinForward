using WinForward.Userland.Configuration;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class ConfigurationArrayTests
{
    internal static void Register()
    {
        TestRunner.Add("config-array/单值与单元素数组等价", ScalarEqualsArray);
        TestRunner.Add("config-array/数组顺序保留且同类去重", ArrayOrderAndDedup);
        TestRunner.Add("config-array/端口数组排序并合并相邻区间", PortMerge);
        TestRunner.Add("config-array/remotePorts 的 * 只能单独出现", StarAlone);
        TestRunner.Add("config-array/端口 0 拒绝并带 JSON 路径", PortZero);
        TestRunner.Add("config-array/五个字段空数组都拒绝", EmptyArrays);
        TestRunner.Add("config-array/空元素拒绝并带 JSON 路径", EmptyElement);
        TestRunner.Add("config-array/非字符串元素拒绝", NonStringElement);
        TestRunner.Add("config-array/未知协议与地址族拒绝", UnknownValues);
        TestRunner.Add("config-array/any 与具体值混写视为不限制", AnyWins);
        TestRunner.Add("config-array/混合族数组原样保留", MixedFamilies);
    }

    private static void ScalarEqualsArray()
    {
        PolicyTable scalar = PolicyConfigurationDocument.Parse("""
            {
              "fallbackAction": "permit",
              "rules": [ {
                "action": "redirect",
                "process": "svchost.exe",
                "remoteNetwork": "10.0.0.0/8",
                "remotePorts": "443",
                "protocol": "tcp",
                "family": "ipv4"
              } ]
            }
            """);
        PolicyTable array = PolicyConfigurationDocument.Parse("""
            {
              "fallbackAction": "permit",
              "rules": [ {
                "action": "redirect",
                "process": ["svchost.exe"],
                "remoteNetwork": ["10.0.0.0/8"],
                "remotePorts": ["443"],
                "protocol": ["tcp"],
                "family": ["ipv4"]
              } ]
            }
            """);

        Check.Equal(Describe(scalar.Rules[0]), Describe(array.Rules[0]), "单值与单元素数组解析成同一规则");
    }

    private static void ArrayOrderAndDedup()
    {
        PolicyTable table = PolicyConfigurationDocument.Parse("""
            {
              "fallbackAction": "permit",
              "rules": [ {
                "action": "redirect",
                "process": ["b.exe", "a.exe", "b.exe"],
                "remoteNetwork": ["10.0.0.0/8", "10.1.2.3/8", "192.168.0.0/16"],
                "remotePorts": ["443", "80"],
                "protocol": ["udp", "tcp", "tcp"],
                "family": ["ipv6", "ipv4", "ipv4"]
              } ]
            }
            """);

        PolicyRule rule = table.Rules[0];
        Check.Equal(3, rule.Processes!.Count, "配置层保留进程选择器数组原样（去重发生在编译期）");
        Check.Equal("b.exe", rule.Processes[0].Value, "保留首次出现顺序");
        Check.Equal("a.exe", rule.Processes[1].Value, "保留书写顺序");
        Check.Equal(2, rule.RemoteNetworks!.Count, "同网段不同主机位去重");
        Check.Equal("10.0.0.0/8", rule.RemoteNetworks[0].ToString(), "网段顺序");
        Check.Equal(2, rule.Protocols!.Count, "协议去重");
        Check.Equal(RuleProtocol.Udp, rule.Protocols[0], "协议顺序");
        Check.Equal(2, rule.Families!.Count, "族去重");
        Check.Equal(RuleAddressFamily.Ipv6, rule.Families[0], "族顺序");
        Check.Equal(2, rule.RemotePorts!.Count, "端口排序后数量");
        Check.Equal((ushort)80, rule.RemotePorts[0].Begin, "端口按起点排序");
    }

    private static void PortMerge()
    {
        PolicyTable table = PolicyConfigurationDocument.Parse("""
            {
              "fallbackAction": "permit",
              "rules": [ { "action": "permit", "remotePorts": ["443", "80-90", "85-100", "1001", "1000"] } ]
            }
            """);

        (ushort, ushort)[] expected = { (80, 100), (443, 443), (1000, 1001) };
        IReadOnlyList<PortRange> ports = table.Rules[0].RemotePorts!;
        Check.Equal(expected.Length, ports.Count, "合并后的区间数");
        for (int index = 0; index < expected.Length; index++)
        {
            PortRange range = ports[index];
            Check.Equal(expected[index], (range.Begin, range.End), $"区间 {index}");
        }
    }

    private static void StarAlone()
    {
        PolicyTable table = PolicyConfigurationDocument.Parse("""
            { "fallbackAction": "permit", "rules": [ { "action": "permit", "remotePorts": ["*"] } ] }
            """);
        Check.True(table.Rules[0].RemotePorts is null, "单独的 * 表示不限制");

        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("""
                { "fallbackAction": "permit", "rules": [ { "action": "permit", "remotePorts": ["*", "443"] } ] }
                """),
            "rules[0].remotePorts[0]", "* 与具体值混写必须拒绝");
    }

    private static void PortZero()
    {
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("""
                { "fallbackAction": "permit", "rules": [ { "action": "permit", "remotePorts": ["0"] } ] }
                """),
            "rules[0].remotePorts[0]", "端口 0 必须拒绝");

        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("""
                { "fallbackAction": "permit", "rules": [ { "action": "permit", "remotePorts": ["0-1"] } ] }
                """),
            "rules[0].remotePorts[0]", "区间端点包含 0 也必须拒绝");
    }

    private static void EmptyArrays()
    {
        string[] fields = { "process", "remoteNetwork", "remotePorts", "protocol", "family" };
        foreach (string field in fields)
        {
            string json = $$"""
                { "fallbackAction": "permit", "rules": [ { "action": "permit", "{{field}}": [] } ] }
                """;
            Check.ThrowsContaining<ConfigurationException>(
                () => PolicyConfigurationDocument.Parse(json),
                $"rules[0].{field}", $"{field} 空数组必须带 JSON 路径拒绝");
        }
    }

    private static void EmptyElement()
    {
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("""
                { "fallbackAction": "permit", "rules": [ { "action": "permit", "process": ["a.exe", "  "] } ] }
                """),
            "rules[0].process[1]", "空元素必须带元素路径");
    }

    private static void NonStringElement()
    {
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("""
                { "fallbackAction": "permit", "rules": [ { "action": "permit", "remotePorts": [443] } ] }
                """),
            "rules[0].remotePorts[0]", "非字符串元素必须拒绝");
    }

    private static void UnknownValues()
    {
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("""
                { "fallbackAction": "permit", "rules": [ { "action": "permit", "protocol": ["tcp", "icmp"] } ] }
                """),
            "rules[0].protocol[1]", "未知协议必须带元素路径");

        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("""
                { "fallbackAction": "permit", "rules": [ { "action": "permit", "family": ["ipv5"] } ] }
                """),
            "rules[0].family[0]", "未知地址族必须带元素路径");
    }

    private static void AnyWins()
    {
        PolicyTable table = PolicyConfigurationDocument.Parse("""
            {
              "fallbackAction": "permit",
              "rules": [ { "action": "permit", "protocol": ["tcp", "any"], "family": ["ipv6", "any"] } ]
            }
            """);

        Check.True(table.Rules[0].Protocols is null, "any 与具体协议混写 = 不限制");
        Check.True(table.Rules[0].Families is null, "any 与具体族混写 = 不限制");
    }

    private static void MixedFamilies()
    {
        PolicyTable table = PolicyConfigurationDocument.Parse("""
            {
              "fallbackAction": "permit",
              "rules": [ { "action": "permit", "remoteNetwork": ["10.0.0.0/8", "2001:db8::/32"] } ]
            }
            """);

        IReadOnlyList<RemoteNetwork> networks = table.Rules[0].RemoteNetworks!;
        Check.Equal(2, networks.Count, "v4 / v6 可混写");
        Check.Equal(RuleAddressFamily.Ipv4, networks[0].Family, "第一条是 v4");
        Check.Equal(RuleAddressFamily.Ipv6, networks[1].Family, "第二条是 v6");
    }

    private static string Describe(PolicyRule rule) => string.Join(
        "|",
        rule.Action,
        string.Join(",", rule.Processes?.Select(selector => selector.Value) ?? Array.Empty<string>()),
        string.Join(",", rule.RemoteNetworks?.Select(network => network.ToString()) ?? Array.Empty<string>()),
        string.Join(",", rule.RemotePorts?.Select(port => port.ToString()) ?? Array.Empty<string>()),
        string.Join(",", rule.Protocols?.Select(protocol => protocol.ToString()) ?? Array.Empty<string>()),
        string.Join(",", rule.Families?.Select(family => family.ToString()) ?? Array.Empty<string>()));
}
