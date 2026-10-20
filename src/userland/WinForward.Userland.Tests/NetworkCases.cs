using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class NetworkCases
{
    internal static IReadOnlyList<DifferentialCase> All { get; } = new[]
    {
        Differential.Case(
            "cidr-v4-slash0-family-v4",
            "IPv4 默认路由：只吃 IPv4 连接，IPv6 连接仍走兜底 permit",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(
                    RuleAction.Redirect, network: "0.0.0.0/0", family: RuleAddressFamily.Ipv4),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Redirect, 0),  // 0  ipv4 10.1.2.3:443
                (1, RuleAction.Redirect, 0),  // 1  ipv4 10.1.2.3:80
                (2, RuleAction.Redirect, 0),  // 2  ipv4 192.168.1.5:8080
                (5, RuleAction.Redirect, 0),  // 5  ipv4 10.1.2.3:1
                (6, RuleAction.Redirect, 0),  // 6  ipv4 10.1.2.3:65535
                (7, RuleAction.Redirect, 0),  // 7  ipv4 10.1.2.3:443 大写路径
                (8, RuleAction.Redirect, 0),  // 8  ipv4 10.1.2.3:443 .bak
                (9, RuleAction.Redirect, 0),  // 9  ipv4 10.1.2.3:443 无进程路径
                (11, RuleAction.Redirect, 0), // 11 ipv4 172.16.0.9:53
                (12, RuleAction.Redirect, 0), // 12 ipv4 10.1.2.3:443 udp
                (13, RuleAction.Redirect, 0), // 13 ipv4 10.1.2.4:443
                (14, RuleAction.Redirect, 0))), // 14 ipv4 203.0.113.9:9000 中文路径

        Differential.Case(
            "cidr-v4-slash32",
            "/32 只匹配一个地址：10.1.2.4 与 IPv6 都不命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, network: "10.1.2.3/32"),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Block, 0),     // 0  ipv4 10.1.2.3:443
                (1, RuleAction.Block, 0),     // 1  ipv4 10.1.2.3:80
                (5, RuleAction.Block, 0),     // 5  ipv4 10.1.2.3:1
                (6, RuleAction.Block, 0),     // 6  ipv4 10.1.2.3:65535
                (7, RuleAction.Block, 0),     // 7  大写路径
                (8, RuleAction.Block, 0),     // 8  .bak
                (9, RuleAction.Block, 0),     // 9  无进程路径
                (12, RuleAction.Block, 0))),  // 12 udp

        Differential.Case(
            "cidr-v4-prefix24-host-bits",
            "/24 写 10.1.2.77：主机位清零后覆盖 10.1.2.0/24，10.1.2.4 也命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, network: "10.1.2.77/24"),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Block, 0),     // 0  ipv4 10.1.2.3:443
                (1, RuleAction.Block, 0),     // 1  ipv4 10.1.2.3:80
                (5, RuleAction.Block, 0),     // 5  ipv4 10.1.2.3:1
                (6, RuleAction.Block, 0),     // 6  ipv4 10.1.2.3:65535
                (7, RuleAction.Block, 0),     // 7  大写路径
                (8, RuleAction.Block, 0),     // 8  .bak
                (9, RuleAction.Block, 0),     // 9  无进程路径
                (12, RuleAction.Block, 0),    // 12 udp
                (13, RuleAction.Block, 0))),  // 13 ipv4 10.1.2.4:443

        Differential.Case(
            "cidr-v6-slash0-family-v6",
            "IPv6 默认路由：只吃 IPv6 连接",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, network: "::/0", family: RuleAddressFamily.Ipv6),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (3, RuleAction.Block, 0),     // 3  ipv6 2001:db8::1:443
                (4, RuleAction.Block, 0),     // 4  ipv6 2001:db8::2:80
                (10, RuleAction.Block, 0))),  // 10 ipv6 2001:db8:1::9:8443

        Differential.Case(
            "cidr-v6-slash128",
            "/128 只匹配一个地址",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, network: "2001:db8::1/128"),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (3, RuleAction.Block, 0))),   // 3 ipv6 2001:db8::1:443

        Differential.Case(
            "cidr-v6-prefix32",
            "/32 覆盖整个 2001:db8 段，三条 IPv6 连接都命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Redirect, network: "2001:db8::/32"),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (3, RuleAction.Redirect, 0),  // 3  ipv6 2001:db8::1:443
                (4, RuleAction.Redirect, 0),  // 4  ipv6 2001:db8::2:80
                (10, RuleAction.Redirect, 0))), // 10 ipv6 2001:db8:1::9:8443

        Differential.Case(
            "family-ipv4-only-no-network",
            "不写网段、只用地址族限制：IPv6 的 443 连接不命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, family: RuleAddressFamily.Ipv4, ports: PortRange.Single(443)),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Block, 0),     // 0  ipv4 443
                (7, RuleAction.Block, 0),     // 7  ipv4 443 大写路径
                (8, RuleAction.Block, 0),     // 8  ipv4 443 .bak
                (9, RuleAction.Block, 0),     // 9  ipv4 443 无进程路径
                (12, RuleAction.Block, 0),    // 12 ipv4 443 udp
                (13, RuleAction.Block, 0))),  // 13 ipv4 443

        Differential.Case(
            "family-any-both-stacks",
            "不写地址族、只写端口：IPv4 与 IPv6 的 443 都命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, ports: PortRange.Single(443)),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Block, 0),     // 0  ipv4 443
                (3, RuleAction.Block, 0),     // 3  ipv6 443
                (7, RuleAction.Block, 0),     // 7  ipv4 443 大写路径
                (8, RuleAction.Block, 0),     // 8  ipv4 443 .bak
                (9, RuleAction.Block, 0),     // 9  ipv4 443 无进程路径
                (12, RuleAction.Block, 0),    // 12 ipv4 443 udp
                (13, RuleAction.Block, 0))),  // 13 ipv4 443

        Differential.Case(
            "network-v4-slash0-family-any",
            "网段自带 IPv4 族、规则族留空：IPv6 连接由网段的族检查挡掉",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, network: "0.0.0.0/0"),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Block, 0),     // 0  ipv4 10.1.2.3:443
                (1, RuleAction.Block, 0),     // 1  ipv4 10.1.2.3:80
                (2, RuleAction.Block, 0),     // 2  ipv4 192.168.1.5:8080
                (5, RuleAction.Block, 0),     // 5  ipv4 10.1.2.3:1
                (6, RuleAction.Block, 0),     // 6  ipv4 10.1.2.3:65535
                (7, RuleAction.Block, 0),     // 7  大写路径
                (8, RuleAction.Block, 0),     // 8  .bak
                (9, RuleAction.Block, 0),     // 9  无进程路径
                (11, RuleAction.Block, 0),    // 11 ipv4 172.16.0.9:53
                (12, RuleAction.Block, 0),    // 12 udp
                (13, RuleAction.Block, 0),    // 13 ipv4 10.1.2.4:443
                (14, RuleAction.Block, 0))),  // 14 ipv4 203.0.113.9:9000 中文路径

        Differential.Case(
            "two-default-routes-block-v6",
            "两条默认路由：0.0.0.0/0 redirect，::/0 block；顺序与族一起决定结果",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Redirect, network: "0.0.0.0/0"),
                Differential.Rule(RuleAction.Block, network: "::/0"),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Redirect, 0),  // 0  ipv4
                (1, RuleAction.Redirect, 0),  // 1  ipv4
                (2, RuleAction.Redirect, 0),  // 2  ipv4
                (3, RuleAction.Block, 1),     // 3  ipv6 落到第二条
                (4, RuleAction.Block, 1),     // 4  ipv6
                (5, RuleAction.Redirect, 0),  // 5  ipv4
                (6, RuleAction.Redirect, 0),  // 6  ipv4
                (7, RuleAction.Redirect, 0),  // 7  ipv4
                (8, RuleAction.Redirect, 0),  // 8  ipv4
                (9, RuleAction.Redirect, 0),  // 9  ipv4
                (10, RuleAction.Block, 1),    // 10 ipv6
                (11, RuleAction.Redirect, 0), // 11 ipv4
                (12, RuleAction.Redirect, 0), // 12 ipv4 udp
                (13, RuleAction.Redirect, 0), // 13 ipv4
                (14, RuleAction.Redirect, 0))), // 14 ipv4 中文路径
    };
}
