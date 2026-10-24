using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class ProcessCases
{
    internal static IReadOnlyList<DifferentialCase> All { get; } = new[]
    {
        Differential.Case(
            "process-file-name-svchost",
            "文件名选择器 svchost.exe：命中任意目录下的同名文件，不命中 .bak 后缀",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Redirect, process: "svchost.exe"),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Redirect, 0),  // 0  ipv4 443 svchost
                (2, RuleAction.Redirect, 0),  // 2  ipv4 8080 svchost
                (3, RuleAction.Redirect, 0),  // 3  ipv6 443 svchost
                (7, RuleAction.Redirect, 0),  // 7  大写路径
                (12, RuleAction.Redirect, 0))), // 12 udp svchost

        Differential.Case(
            "process-file-name-requires-same-segment",
            "文件名选择器 svchost：不命中 svchost.exe，也不命中 svchost.exe.bak",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Redirect, process: "svchost"),
            },
            Differential.Expectations(RuleAction.Permit)),

        Differential.Case(
            "process-directory-system32",
            "目录选择器 C:\\Windows\\System32：命中直接子文件与深层后代，含 .bak 与无扩展名形态",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Redirect, process: @"C:\Windows\System32"),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Redirect, 0),  // 0  ipv4 443 svchost
                (2, RuleAction.Redirect, 0),  // 2  ipv4 8080 svchost
                (3, RuleAction.Redirect, 0),  // 3  ipv6 443 svchost
                (7, RuleAction.Redirect, 0),  // 7  大写路径
                (8, RuleAction.Redirect, 0),  // 8  .bak
                (12, RuleAction.Redirect, 0))), // 12 udp svchost

        Differential.Case(
            "process-array-or",
            "进程数组 [svchost.exe, c:\\other.exe]：数组内 OR，文件名与精确路径混用",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Redirect, processList: new[] { "svchost.exe", @"c:\other.exe" }),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Redirect, 0),  // 0  svchost
                (2, RuleAction.Redirect, 0),  // 2  svchost
                (3, RuleAction.Redirect, 0),  // 3  svchost
                (5, RuleAction.Redirect, 0),  // 5  other
                (6, RuleAction.Redirect, 0),  // 6  other
                (7, RuleAction.Redirect, 0),  // 7  svchost 大写
                (10, RuleAction.Redirect, 0), // 10 other ipv6
                (11, RuleAction.Redirect, 0), // 11 other
                (12, RuleAction.Redirect, 0), // 12 svchost udp
                (13, RuleAction.Redirect, 0))), // 13 other

        Differential.Case(
            "block-exact-path-array",
            "block 规则写精确文件路径数组：两条路径各自生效，未写进数组的进程不命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(RuleAction.Block, processList: new[] { Differential.Svchost, Differential.App }),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Block, 0),  // 0  svchost
                (1, RuleAction.Block, 0),  // 1  app
                (2, RuleAction.Block, 0),  // 2  svchost
                (3, RuleAction.Block, 0),  // 3  svchost
                (4, RuleAction.Block, 0),  // 4  app
                (7, RuleAction.Block, 0),  // 7  svchost 大写
                (12, RuleAction.Block, 0))), // 12 svchost udp

        Differential.Case(
            "array-and-or-matrix",
            "进程 / 网段 / 端口 / 协议 / 族五个数组同时出现：字段内 OR、字段间 AND",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(
                    RuleAction.Redirect,
                    processList: new[] { "svchost.exe" },
                    networkList: new[] { "10.1.0.0/16", "192.168.0.0/16" },
                    portList: new[] { PortRange.Single(80), PortRange.Single(443), new PortRange(8000, 8100) },
                    protocolList: new[] { RuleProtocol.Tcp },
                    familyList: new[] { RuleAddressFamily.Ipv4 }),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Redirect, 0),  // 0  10.1.2.3:443 svchost
                (2, RuleAction.Redirect, 0),  // 2  192.168.1.5:8080 svchost
                (7, RuleAction.Redirect, 0))), // 7  10.1.2.3:443 svchost 大写

        Differential.Case(
            "network-array-mixed-family",
            "网段数组混写 v4 与 v6，族数组允许两者：同一规则按连接族走不同记录",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(
                    RuleAction.Redirect,
                    networkList: new[] { "10.1.0.0/16", "2001:db8::/32" },
                    familyList: new[] { RuleAddressFamily.Ipv4, RuleAddressFamily.Ipv6 }),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Redirect, 0),  // 0  ipv4 10.1.2.3
                (1, RuleAction.Redirect, 0),  // 1  ipv4 10.1.2.3
                (3, RuleAction.Redirect, 0),  // 3  ipv6 2001:db8::1
                (4, RuleAction.Redirect, 0),  // 4  ipv6 2001:db8::2
                (5, RuleAction.Redirect, 0),  // 5  ipv4 10.1.2.3
                (6, RuleAction.Redirect, 0),  // 6  ipv4 10.1.2.3
                (7, RuleAction.Redirect, 0),  // 7  ipv4 10.1.2.3
                (8, RuleAction.Redirect, 0),  // 8  ipv4 10.1.2.3
                (9, RuleAction.Redirect, 0),  // 9  ipv4 10.1.2.3
                (10, RuleAction.Redirect, 0), // 10 ipv6 2001:db8:1::9
                (12, RuleAction.Redirect, 0), // 12 ipv4 udp
                (13, RuleAction.Redirect, 0))), // 13 ipv4 10.1.2.4

        Differential.Case(
            "multi-port-array",
            "端口数组 [80, 443, 8000-8100]：区间端点与区间内端口都命中",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(
                    RuleAction.Redirect,
                    portList: new[] { PortRange.Single(80), PortRange.Single(443), new PortRange(8000, 8100) }),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Redirect, 0),  // 0  443
                (1, RuleAction.Redirect, 0),  // 1  80
                (2, RuleAction.Redirect, 0),  // 2  8080
                (3, RuleAction.Redirect, 0),  // 3  443 ipv6
                (4, RuleAction.Redirect, 0),  // 4  80 ipv6
                (7, RuleAction.Redirect, 0),  // 7  443
                (8, RuleAction.Redirect, 0),  // 8  443
                (9, RuleAction.Redirect, 0),  // 9  443
                (12, RuleAction.Redirect, 0), // 12 443 udp
                (13, RuleAction.Redirect, 0))), // 13 443

        Differential.Case(
            "protocol-array-tcp-udp",
            "协议数组 [tcp, udp]：两种协议都命中，端口数组只放行 80 与 443",
            RuleAction.Permit,
            new[]
            {
                Differential.Rule(
                    RuleAction.Redirect,
                    portList: new[] { PortRange.Single(80), PortRange.Single(443) },
                    protocolList: new[] { RuleProtocol.Tcp, RuleProtocol.Udp }),
            },
            Differential.Expectations(
                RuleAction.Permit,
                (0, RuleAction.Redirect, 0),  // 0  443 tcp
                (1, RuleAction.Redirect, 0),  // 1  80 tcp
                (3, RuleAction.Redirect, 0),  // 3  443 tcp ipv6
                (4, RuleAction.Redirect, 0),  // 4  80 tcp ipv6
                (7, RuleAction.Redirect, 0),  // 7  443 tcp
                (8, RuleAction.Redirect, 0),  // 8  443 tcp
                (9, RuleAction.Redirect, 0),  // 9  443 tcp
                (12, RuleAction.Redirect, 0), // 12 443 udp
                (13, RuleAction.Redirect, 0))), // 13 443 tcp
    };
}
