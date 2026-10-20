using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class TransportCases
{
    internal static IReadOnlyList<DifferentialCase> All { get; } = new[]
    {
        Differential.Case(
            "port-single-443",
            "端口闭区间 443..443：只有 443 端点命中，80 / 8080 / 8443 都不命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Redirect, ports: PortRange.Single(443)),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Redirect, 0),  // 0  ipv4 443
                (3, RuleAction.Redirect, 0),  // 3  ipv6 443
                (7, RuleAction.Redirect, 0),  // 7  ipv4 443 大写路径
                (8, RuleAction.Redirect, 0),  // 8  ipv4 443 .bak
                (9, RuleAction.Redirect, 0),  // 9  ipv4 443 无进程路径
                (12, RuleAction.Redirect, 0), // 12 ipv4 443 udp
                (13, RuleAction.Redirect, 0))), // 13 ipv4 443

        Differential.Case(
            "port-range-endpoints-excluded",
            "端口区间 2..65534：端口 1 与 65535 在闭区间之外，必须落到兜底",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, ports: new PortRange(2, 65534)),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Block, 0),     // 0  ipv4 443
                (1, RuleAction.Block, 0),     // 1  ipv4 80
                (2, RuleAction.Block, 0),     // 2  ipv4 8080
                (3, RuleAction.Block, 0),     // 3  ipv6 443
                (4, RuleAction.Block, 0),     // 4  ipv6 80
                (7, RuleAction.Block, 0),     // 7  ipv4 443
                (8, RuleAction.Block, 0),     // 8  ipv4 443
                (9, RuleAction.Block, 0),     // 9  ipv4 443
                (10, RuleAction.Block, 0),    // 10 ipv6 8443
                (11, RuleAction.Block, 0),    // 11 ipv4 53
                (12, RuleAction.Block, 0),    // 12 ipv4 443
                (13, RuleAction.Block, 0),    // 13 ipv4 443
                (14, RuleAction.Block, 0))),  // 14 ipv4 9000 中文路径

        Differential.Case(
            "port-any-explicit",
            "显式写 0..65535：与「任意端口」哨兵等价，全部连接命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, ports: PortRange.Any),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Block, 0),     // 0
                (1, RuleAction.Block, 0),     // 1
                (2, RuleAction.Block, 0),     // 2
                (3, RuleAction.Block, 0),     // 3
                (4, RuleAction.Block, 0),     // 4
                (5, RuleAction.Block, 0),     // 5 端口 1
                (6, RuleAction.Block, 0),     // 6 端口 65535
                (7, RuleAction.Block, 0),     // 7
                (8, RuleAction.Block, 0),     // 8
                (9, RuleAction.Block, 0),     // 9
                (10, RuleAction.Block, 0),    // 10
                (11, RuleAction.Block, 0),    // 11
                (12, RuleAction.Block, 0),    // 12
                (13, RuleAction.Block, 0),    // 13
                (14, RuleAction.Block, 0))),  // 14 中文路径

        Differential.Case(
            "process-rule-uppercase",
            "规则串写成全大写：编译期折成小写，大小写不同的连接都命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, process: @"C:\WINDOWS\SYSTEM32\SVCHOST.EXE"),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Block, 0),     // 0  ipv4 443 svchost
                (2, RuleAction.Block, 0),     // 2  ipv4 8080 svchost
                (3, RuleAction.Block, 0),     // 3  ipv6 443 svchost
                (7, RuleAction.Block, 0),     // 7  大写路径
                (12, RuleAction.Block, 0))),  // 12 udp svchost

        Differential.Case(
            "process-exact-not-prefix",
            "规则串少了 .exe：进程比较是整串相等，不是前缀匹配",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, process: @"c:\windows\system32\svchost"),
            },
            Differential.Expectations(RuleAction.Permit)),

        Differential.Case(
            "process-rule-with-bak-suffix",
            "规则串多一段 .bak：只命中同名但后缀不同的那条连接",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, process: Differential.Svchost + ".bak"),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (8, RuleAction.Block, 0))),   // 8 ipv4 443 svchost.exe.bak

        Differential.Case(
            "protocol-tcp-excludes-udp",
            "进程规则限定 TCP：同一进程的 udp 连接不命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, process: Differential.Svchost, protocol: RuleProtocol.Tcp),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Block, 0),     // 0  ipv4 tcp svchost
                (2, RuleAction.Block, 0),     // 2  ipv4 tcp svchost
                (3, RuleAction.Block, 0),     // 3  ipv6 tcp svchost
                (7, RuleAction.Block, 0))),   // 7  大写路径 tcp

        Differential.Case(
            "protocol-any-includes-udp",
            "进程规则不限协议：同一进程的 udp 连接也命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, process: Differential.Svchost, protocol: RuleProtocol.Any),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Block, 0),     // 0  ipv4 tcp svchost
                (2, RuleAction.Block, 0),     // 2  ipv4 tcp svchost
                (3, RuleAction.Block, 0),     // 3  ipv6 tcp svchost
                (7, RuleAction.Block, 0),     // 7  大写路径 tcp
                (12, RuleAction.Block, 0))),  // 12 ipv4 udp svchost

        Differential.Case(
            "port-zero-single-not-any",
            "端口 0..0 是「只有 0 号端口」，不是「任意端口」：没有连接命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, ports: new PortRange(0, 0)),
            },
            Differential.Expectations(RuleAction.Permit)),

        Differential.Case(
            "port-max-only",
            "端口 65535..65535：只有端口上界那条连接命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, ports: PortRange.Single(65535)),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (6, RuleAction.Block, 0))),   // 6 ipv4 10.1.2.3:65535 other
        Differential.Case(
            "process-unicode-path",
            "进程串含中文：按 UTF-8 落盘、只折叠 ASCII 大小写，连接侧大小写不同仍应命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, process: @"c:\users\张三\工具.exe"),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (14, RuleAction.Block, 0))),  // 14 C:\Users\张三\工具.EXE
    };

}
