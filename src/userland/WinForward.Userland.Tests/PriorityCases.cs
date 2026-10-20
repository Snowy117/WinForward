using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class PriorityCases
{
    internal static IReadOnlyList<DifferentialCase> All { get; } = new[]
    {
        Differential.Case(
            "empty-table-block",
            "空规则表：15 条连接全部落到兜底 block",
            RuleAction.Block,
            Array.Empty<PolicyRule>(),
            Differential.Expectations(RuleAction.Block)),

        Differential.Case(
            "empty-table-redirect",
            "空规则表：兜底动作改成 redirect，头部 fallback_action 必须跟着走",
            RuleAction.Redirect,
            Array.Empty<PolicyRule>(),
            Differential.Expectations(RuleAction.Redirect)),

        Differential.Case(
            "fallback-permit-process",
            "单条 svchost redirect 规则；只有 svchost 连接命中，其余走兜底 permit",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Redirect, process: Differential.Svchost),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Redirect, 0),  // 0 ipv4 10.1.2.3:443 svchost
                (2, RuleAction.Redirect, 0),  // 2 ipv4 192.168.1.5:8080 svchost
                (3, RuleAction.Redirect, 0),  // 3 ipv6 2001:db8::1:443 svchost
                (7, RuleAction.Redirect, 0),  // 7 ipv4 10.1.2.3:443 大写路径
                (12, RuleAction.Redirect, 0))), // 12 ipv4 10.1.2.3:443 udp svchost

        Differential.Case(
            "priority-first-match-wins",
            "两条规则都能匹配 svchost：数组顺序即优先级，先出现的 block 生效",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, process: Differential.Svchost),
                Differential.Rule(RuleAction.Redirect, process: Differential.Svchost, ports: PortRange.Single(443)),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Block, 0),   // 0 ipv4 443 svchost
                (2, RuleAction.Block, 0),   // 2 ipv4 8080 svchost
                (3, RuleAction.Block, 0),   // 3 ipv6 443 svchost
                (7, RuleAction.Block, 0),   // 7 大写路径
                (12, RuleAction.Block, 0))), // 12 udp svchost

        Differential.Case(
            "priority-specific-rule-first",
            "把具体规则（443）放在前面：443 命中 redirect，其余 svchost 落到第二条 block",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Redirect, process: Differential.Svchost, ports: PortRange.Single(443)),
                Differential.Rule(RuleAction.Block, process: Differential.Svchost),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Redirect, 0),  // 0 ipv4 443 svchost
                (2, RuleAction.Block, 1),     // 2 ipv4 8080 svchost
                (3, RuleAction.Redirect, 0),  // 3 ipv6 443 svchost
                (7, RuleAction.Redirect, 0),  // 7 大写路径
                (12, RuleAction.Redirect, 0))), // 12 udp 443 svchost

        Differential.Case(
            "mixed-rules-report-rule-index",
            "三条互不相同的规则 + 兜底 block：逐条连接检查命中的规则序号",
            RuleAction.Block,
            new[]
            {
                Differential.Rule(RuleAction.Redirect, network: "10.1.0.0/16"),
                Differential.Rule(RuleAction.Block, ports: PortRange.Single(80)),
                Differential.Rule(RuleAction.Redirect, family: RuleAddressFamily.Ipv6),
            },
            Differential.Expectations(
                RuleAction.Block,
                (0, RuleAction.Redirect, 0),  // 0 ipv4 10.1.2.3:443
                (1, RuleAction.Redirect, 0),  // 1 ipv4 10.1.2.3:80 先命中规则 0
                (3, RuleAction.Redirect, 2),  // 3 ipv6 443 落到族规则
                (4, RuleAction.Block, 1),     // 4 ipv6 80 命中端口规则
                (5, RuleAction.Redirect, 0),  // 5 ipv4 10.1.2.3:1
                (6, RuleAction.Redirect, 0),  // 6 ipv4 10.1.2.3:65535
                (7, RuleAction.Redirect, 0),  // 7 大写路径
                (8, RuleAction.Redirect, 0),  // 8 .bak 后缀
                (9, RuleAction.Redirect, 0),  // 9 无进程路径
                (10, RuleAction.Redirect, 2), // 10 ipv6 8443
                (12, RuleAction.Redirect, 0), // 12 udp 10.1.2.3:443
                (13, RuleAction.Redirect, 0))), // 13 ipv4 10.1.2.4:443

        Differential.Case(
            "network-record-dedup-two-rules",
            "两条规则写同一个 /16 的不同主机位：网段池去重成一条记录，优先级仍按书写顺序",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, network: "10.1.0.0/16", ports: PortRange.Single(80)),
                Differential.Rule(RuleAction.Redirect, network: "10.1.77.88/16"),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Redirect, 1),  // 0 ipv4 10.1.2.3:443
                (1, RuleAction.Block, 0),     // 1 ipv4 10.1.2.3:80
                (5, RuleAction.Redirect, 1),  // 5 ipv4 10.1.2.3:1
                (6, RuleAction.Redirect, 1),  // 6 ipv4 10.1.2.3:65535
                (7, RuleAction.Redirect, 1),  // 7 大写路径
                (8, RuleAction.Redirect, 1),  // 8 .bak 后缀
                (9, RuleAction.Redirect, 1),  // 9 无进程路径
                (12, RuleAction.Redirect, 1), // 12 udp 10.1.2.3:443
                (13, RuleAction.Redirect, 1))), // 13 ipv4 10.1.2.4:443

        Differential.Case(
            "process-pool-dedup-two-rules",
            "两条规则写大小写不同的同一进程串：池内去重，签名相同的连接两条都能命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Redirect, process: Differential.Svchost, ports: PortRange.Single(443)),
                Differential.Rule(RuleAction.Block, process: @"C:\WINDOWS\SYSTEM32\SVCHOST.EXE", ports: PortRange.Single(8080)),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Redirect, 0),  // 0 ipv4 443 svchost
                (2, RuleAction.Block, 1),     // 2 ipv4 8080 svchost
                (3, RuleAction.Redirect, 0),  // 3 ipv6 443 svchost
                (7, RuleAction.Redirect, 0),  // 7 大写路径
                (12, RuleAction.Redirect, 0))), // 12 udp 443 svchost

        Differential.Case(
            "all-any-single-rule",
            "一条什么选择器都不写的规则：任意进程、任意网段、任意端口、任意协议、任意族",
            RuleAction.Block,
            new[]
            {
                Differential.Rule(RuleAction.Redirect),
            },
            Differential.Expectations(
                RuleAction.Block,
                (0, RuleAction.Redirect, 0),  // 0
                (1, RuleAction.Redirect, 0),  // 1
                (2, RuleAction.Redirect, 0),  // 2
                (3, RuleAction.Redirect, 0),  // 3
                (4, RuleAction.Redirect, 0),  // 4
                (5, RuleAction.Redirect, 0),  // 5
                (6, RuleAction.Redirect, 0),  // 6
                (7, RuleAction.Redirect, 0),  // 7
                (8, RuleAction.Redirect, 0),  // 8
                (9, RuleAction.Redirect, 0),  // 9
                (10, RuleAction.Redirect, 0), // 10
                (11, RuleAction.Redirect, 0), // 11
                (12, RuleAction.Redirect, 0), // 12 udp
                (13, RuleAction.Redirect, 0),  // 13
                (14, RuleAction.Redirect, 0))), // 14 中文路径

        Differential.Case(
            "protocol-tcp-rule-then-any",
            "先一条 TCP 规则、再一条任意协议规则：udp 连接落到第二条",
            RuleAction.Block,
            new[]
            {
                Differential.Rule(RuleAction.Block, protocol: RuleProtocol.Tcp),
                Differential.Rule(RuleAction.Permit),
            },
            Differential.Expectations(
                RuleAction.Block,
                (0, RuleAction.Block, 0),     // 0 tcp
                (1, RuleAction.Block, 0),     // 1 tcp
                (2, RuleAction.Block, 0),     // 2 tcp
                (3, RuleAction.Block, 0),     // 3 tcp
                (4, RuleAction.Block, 0),     // 4 tcp
                (5, RuleAction.Block, 0),     // 5 tcp
                (6, RuleAction.Block, 0),     // 6 tcp
                (7, RuleAction.Block, 0),     // 7 tcp
                (8, RuleAction.Block, 0),     // 8 tcp
                (9, RuleAction.Block, 0),     // 9 tcp
                (10, RuleAction.Block, 0),    // 10 tcp
                (11, RuleAction.Block, 0),    // 11 tcp
                (12, RuleAction.Permit, 1),   // 12 udp 落到「任意协议」
                (13, RuleAction.Block, 0),    // 13 tcp
                (14, RuleAction.Block, 0))),  // 14 tcp 中文路径
        Differential.Case(
            "process-and-network-together",
            "同一条规则同时带进程、网段与端口：三个选择器都满足才命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, process: Differential.Svchost, network: "10.1.0.0/16", ports: PortRange.Single(443)),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Block, 0),     // 0  ipv4 10.1.2.3:443 svchost 三个都满足
                (7, RuleAction.Block, 0),     // 7  大写路径 443 svchost
                (12, RuleAction.Block, 0))),  // 12 udp 10.1.2.3:443 svchost
    };
}
