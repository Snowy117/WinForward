using WinForward.Userland.Configuration;

namespace WinForward.Userland.Tests;

/// <summary>配置层的 <c>socks5</c> 段：可选、字段封闭、端口在 1..65535。没写就是直连。</summary>
internal static class Socks5ConfigurationTests
{
    internal static void Register()
    {
        TestRunner.Add("config/socks5 段解析出上游主机与端口", ParsesEndpoint);
        TestRunner.Add("config/没有 socks5 段时上游为 null（直连）", AbsentMeansDirect);
        TestRunner.Add("config/socks5 段未知字段带路径报错", UnknownField);
        TestRunner.Add("config/socks5 缺少 host 报错", MissingHost);
        TestRunner.Add("config/socks5 端口越界报错", PortOutOfRange);
        TestRunner.Add("config/socks5 端口不是整数报错", PortNotInteger);
        TestRunner.Add("config/socks5 不是对象报错", NotAnObject);
    }

    private static void ParsesEndpoint()
    {
        ControllerConfiguration configuration = PolicyConfigurationDocument.ParseConfiguration("""
            {
              "fallbackAction": "permit",
              "socks5": { "host": "127.0.0.1", "port": 1080 },
              "rules": [ { "action": "redirect", "remotePorts": "40001" } ]
            }
            """);

        Check.Equal("127.0.0.1", configuration.Socks5?.Host ?? "null", "上游主机");
        Check.Equal(1080, configuration.Socks5?.Port ?? 0, "上游端口");
        Check.Equal(1, configuration.Policy.Rules.Count, "规则数与 socks5 段共存");
    }

    private static void AbsentMeansDirect()
    {
        ControllerConfiguration configuration = PolicyConfigurationDocument.ParseConfiguration("""
            { "fallbackAction": "permit", "rules": [] }
            """);

        Check.True(configuration.Socks5 is null, "没有 socks5 段必须是 null（直连），不能补一个默认上游");
    }

    private static void UnknownField() =>
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.ParseConfiguration("""
                { "fallbackAction": "permit", "socks5": { "host": "127.0.0.1", "port": 1080, "user": "x" } }
                """),
            "socks5.user", "未知字段必须报出 JSON 路径");

    private static void MissingHost() =>
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.ParseConfiguration("""
                { "fallbackAction": "permit", "socks5": { "port": 1080 } }
                """),
            "socks5.host", "缺少 host");

    private static void PortOutOfRange() =>
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.ParseConfiguration("""
                { "fallbackAction": "permit", "socks5": { "host": "127.0.0.1", "port": 65536 } }
                """),
            "socks5.port", "端口越界");

    private static void PortNotInteger() =>
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.ParseConfiguration("""
                { "fallbackAction": "permit", "socks5": { "host": "127.0.0.1", "port": "1080" } }
                """),
            "socks5.port", "端口必须是整数");

    private static void NotAnObject() =>
        Check.ThrowsContaining<ConfigurationException>(
            () => PolicyConfigurationDocument.ParseConfiguration("""
                { "fallbackAction": "permit", "socks5": "127.0.0.1:1080" }
                """),
            "socks5", "socks5 必须是对象");
}
