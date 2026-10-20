using WinForward.Userland.Configuration;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class ConfigurationTests
{
    internal static void Register()
    {
        TestRunner.Add("config/完整规则逐字段解析", FullRule);
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
        TestRunner.Add("config/端口区间颠倒交给编译器并带序号", ReversedPorts);
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
        Check.Equal(RuleProtocol.Tcp, rule.Protocol, "protocol");
        Check.Equal(RuleAddressFamily.Ipv4, rule.Family, "family");
        Check.Equal((ushort)8000, rule.RemotePorts.Begin, "端口起点");
        Check.Equal((ushort)8100, rule.RemotePorts.End, "端口终点");
        Check.Equal("10.1.0.0/16", rule.RemoteNetwork?.ToString() ?? "null", "网段主机位清零");
        Check.Equal(@"C:\Tools\app.exe", rule.Process?.Path ?? "null", "进程路径");
    }

    private static void Defaults()
    {
        PolicyTable table = PolicyConfigurationDocument.Parse("""
            { "fallbackAction": "permit", "rules": [ { "action": "block" } ] }
            """);

        PolicyRule rule = table.Rules[0];
        Check.Equal(RuleProtocol.Any, rule.Protocol, "协议默认任意");
        Check.Equal(RuleAddressFamily.Any, rule.Family, "地址族默认任意");
        Check.True(rule.RemotePorts.IsAny, "端口默认任意");
        Check.True(rule.RemoteNetwork is null, "网段默认没有");
        Check.True(rule.Process is null, "进程默认没有");
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

        Check.True(table.Rules[0].RemotePorts.IsAny, "* 是任意端口");
        Check.Equal((ushort)443, table.Rules[1].RemotePorts.Begin, "单端口起点");
        Check.Equal((ushort)443, table.Rules[1].RemotePorts.End, "单端口终点");
        Check.Equal((ushort)1000, table.Rules[2].RemotePorts.Begin, "区间起点");
        Check.Equal((ushort)2000, table.Rules[2].RemotePorts.End, "区间终点");
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

    private static void ReversedPorts()
    {
        PolicyTable table = PolicyConfigurationDocument.Parse("""
            {
              "fallbackAction": "permit",
              "rules": [
                { "action": "permit" },
                { "action": "redirect", "remotePorts": "9000-8000" }
              ]
            }
            """);

        Check.Equal((ushort)9000, table.Rules[1].RemotePorts.Begin, "结构层原样保留起点");
        Check.ThrowsContaining<PolicyCompileException>(
            () => TestAppId.Compile(table), "#1", "端口区间颠倒必须带规则序号");
    }

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

    private static void ControlCharacterPath() =>
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.Parse("""
                { "fallbackAction": "permit", "rules": [ { "action": "block", "process": "C:\\bad\u0001path.exe" } ] }
                """),
            "rules[0].process", "控制字符无法写进进程池");
}
