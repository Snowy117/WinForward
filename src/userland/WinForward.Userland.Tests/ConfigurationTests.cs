using WinForward.Userland.Configuration;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class ConfigurationTests
{
    internal static void Register()
    {
        TestRunner.Add("config/完整规则逐字段解析", FullRule);
        TestRunner.Add("config/protocol 接受 udp", UdpProtocol);
        TestRunner.Add("config/省略字段走默认值", Defaults);
        TestRunner.Add("config/端口写法 *、单端口、区间", PortForms);
        TestRunner.Add("config/未知字段带 JSON 路径报错", UnknownField);
        TestRunner.Add("config/缺少兜底动作报错", MissingFallback);
        TestRunner.Add("config/缺少动作报错并带规则序号", MissingAction);
        TestRunner.Add("config/未知动作报错", UnknownAction);
        TestRunner.Add("config/端口不是整数报错", BadPort);
        TestRunner.Add("config/非法 CIDR 报错", BadNetwork);
        TestRunner.Add("config/JSON 语法错误报错", BadJson);
        TestRunner.Add("config/根节点必须是对象", RootNotObject);
        TestRunner.Add("config/端口区间颠倒报 JSON 路径", ReversedPorts);
        TestRunner.Add("config/从文件读取配置", LoadFromFile);
        TestRunner.Add("config/进程路径含控制字符报错", ControlCharacterPath);
    }

    private static void FullRule()
    {
        PolicyTable table = PolicyConfigurationDocument.Parse("""
            {
              "fallbackAction": "block",
              "rules": [
                {
                  "name": "web",
                  "action": "redirect",
                  "protocol": "tcp",
                  "family": "ipv4",
                  "remotePorts": "8000-8100",
                  "remoteNetwork": "10.1.2.3/16",
                  "process": "C:\\Tools\\app.exe"
                }
              ]
            }
            """);

        Check.Equal(1, table.Rules.Count, "规则数");
        Check.Equal(RuleAction.Block, table.FallbackAction, "兜底动作");
        PolicyRule rule = table.Rules[0];
        Check.Equal("web", rule.Name, "name");
        Check.Equal(RuleAction.Redirect, rule.Action, "action");
        Check.Equal((RuleProtocol.Tcp, 1), (rule.Protocols![0], rule.Protocols.Count), "protocol");
        Check.Equal((RuleAddressFamily.Ipv4, 1), (rule.Families![0], rule.Families.Count), "family");
        Check.Equal((ushort)8000, rule.RemotePorts![0].Begin, "端口起点");
        Check.Equal((ushort)8100, rule.RemotePorts[0].End, "端口终点");
        Check.Equal("10.1.0.0/16", rule.RemoteNetworks![0].ToString(), "网段主机位清零");
        Check.Equal(@"C:\Tools\app.exe", rule.Processes![0].Value, "进程路径");
    }

    private static void UdpProtocol()
    {
        PolicyTable table = PolicyConfigurationDocument.Parse("""
            { "fallbackAction": "permit", "rules": [ { "action": "block", "protocol": "udp" } ] }
            """);

        IReadOnlyList<RuleProtocol> protocols = table.Rules[0].Protocols!;
        Check.Equal(RuleProtocol.Udp, protocols[0], "protocol 解析为 udp");
        Check.Equal((byte)17, (byte)protocols[0], "UDP 枚举值 17");
    }

    private static void Defaults()
    {
        PolicyTable table = PolicyConfigurationDocument.Parse("""
            { "fallbackAction": "permit", "rules": [ { "action": "block" } ] }
            """);

        PolicyRule rule = table.Rules[0];
        Check.True(rule.Protocols is null, "协议默认任意");
        Check.True(rule.Families is null, "地址族默认任意");
        Check.True(rule.RemotePorts is null, "端口默认任意");
        Check.True(rule.RemoteNetworks is null, "网段默认没有");
        Check.True(rule.Processes is null, "进程默认没有");
        Check.True(rule.Name is null, "名字默认没有");
        Check.Equal(RuleAction.Block, rule.Action, "动作");
    }

    private static void PortForms()
    {
        PolicyTable table = PolicyConfigurationDocument.Parse("""
            {
              "fallbackAction": "permit",
              "rules": [
                { "action": "permit", "remotePorts": "*" },
                { "action": "permit", "remotePorts": "443" },
                { "action": "permit", "remotePorts": "1000-2000" }
              ]
            }
            """);

        Check.True(table.Rules[0].RemotePorts is null, "* 是任意端口（数组为 null）");
        IReadOnlyList<PortRange> single = table.Rules[1].RemotePorts!;
        IReadOnlyList<PortRange> range = table.Rules[2].RemotePorts!;
        Check.Equal((ushort)443, single[0].Begin, "单端口起点");
        Check.Equal((ushort)443, single[0].End, "单端口终点");
        Check.Equal((ushort)1000, range[0].Begin, "区间起点");
        Check.Equal((ushort)2000, range[0].End, "区间终点");
    }

    private static void UnknownField() =>
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("""
                { "fallbackAction": "permit", "rules": [ { "action": "permit", "remotePort": 80 } ] }
                """),
            "rules[0].remotePort", "未知字段必须报出 JSON 路径");

    private static void MissingFallback() =>
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("""{ "rules": [] }"""),
            "fallbackAction", "缺少兜底动作");

    private static void MissingAction() =>
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("""
                { "fallbackAction": "permit", "rules": [ { "action": "permit" }, { "name": "without-action" } ] }
                """),
            "rules[1].action", "缺少动作必须指出第几条规则");

    private static void UnknownAction() =>
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("""
                { "fallbackAction": "permit", "rules": [ { "action": "drop" } ] }
                """),
            "drop", "未知动作");

    private static void BadPort() =>
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("""
                { "fallbackAction": "permit", "rules": [ { "action": "permit", "remotePorts": "http" } ] }
                """),
            "rules[0].remotePorts", "端口不是整数");

    private static void BadNetwork() =>
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("""
                { "fallbackAction": "permit", "rules": [ { "action": "permit", "remoteNetwork": "10.0.0.0" } ] }
                """),
            "rules[0].remoteNetwork", "非法 CIDR");

    private static void BadJson() =>
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("{ \"fallbackAction\": "), "JSON", "JSON 语法错误");

    private static void RootNotObject() =>
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("[]"), "根节点", "根节点必须是对象");

    private static void ReversedPorts() =>
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("""
                {
                  "fallbackAction": "permit",
                  "rules": [
                    { "action": "permit" },
                    { "action": "redirect", "remotePorts": "9000-8000" }
                  ]
                }
                """),
            "rules[1].remotePorts", "端口区间颠倒必须带精确 JSON 路径");

    private static void LoadFromFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wf-config-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                { "fallbackAction": "permit", "rules": [ { "action": "redirect", "remotePorts": "80" } ] }
                """);
            PolicyTable table = PolicyConfigurationDocument.Load(path);
            Check.Equal(1, table.Rules.Count, "从文件读到的规则数");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void ControlCharacterPath()
    {
        foreach (string code in new[] { "0001", "007F", "0085" })
        {
            string json = $$"""
                { "fallbackAction": "permit", "rules": [ { "action": "block", "process": "C:\\bad\u{{code}}path.exe" } ] }
                """;
            Check.ThrowsContaining<ConfigurationException>(
                () => PolicyConfigurationDocument.Parse(json),
                "rules[0].process", $"控制字符 U+{code} 无法写进进程池");
        }
    }
}
