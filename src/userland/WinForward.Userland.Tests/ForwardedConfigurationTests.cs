using System.Runtime.InteropServices;
using WinForward.Userland.Configuration;
using WinForward.Userland.Control;
using WinForward.Userland.Forwarded;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

/// <summary>
/// B2：forwarded 段、socks5Servers 多上游与 adapter 解析的配置层行为。
/// 这里只覆盖配置 → 模型 → blob 这一段；驱动下发属于 B4。
/// </summary>
internal static class ForwardedConfigurationTests
{
    private const string MinimalJson = """
        {
          "fallbackAction": "permit",
          "socks5Servers": [
            { "name": "main", "host": "127.0.0.1", "port": 1080 }
          ],
          "forwarded": {
            "fallbackAction": "block",
            "rules": [
              {
                "name": "vm-tcp",
                "adapterName": "vEthernet (MyVM)",
                "clientCidr": "10.0.0.0/24",
                "remoteCidr": "203.0.113.0/24",
                "remotePorts": "443",
                "protocol": "tcp",
                "family": "ipv4",
                "action": "redirect",
                "target": "main"
              }
            ]
          }
        }
        """;

    internal static void Register()
    {
        TestRunner.Add("forwarded-config/最小配置解析成功", MinimalConfig);
        TestRunner.Add("forwarded-config/省略字段走默认值", Defaults);
        TestRunner.Add("forwarded-config/多上游按数组顺序分配 id", MultipleUpstreams);
        TestRunner.Add("forwarded-config/旧 socks5 作为 default 上游", LegacySocks5AsDefault);
        TestRunner.Add("forwarded-config/旧配置没有 forwarded 时表为 null", LegacyConfig);
        TestRunner.Add("forwarded-config/forwarded 规则拒绝 process 字段", ProcessRejected);
        TestRunner.Add("forwarded-config/redirect 必须带 target", RedirectWithoutTarget);
        TestRunner.Add("forwarded-config/permit 不得带 target", PermitWithTarget);
        TestRunner.Add("forwarded-config/target 不存在报规则路径", UnknownTarget);
        TestRunner.Add("forwarded-config/上游重名报数组路径", DuplicateUpstream);
        TestRunner.Add("forwarded-config/旧 socks5 与 default 冲突", LegacyDefaultConflict);
        TestRunner.Add("forwarded-config/adapterName 经 resolver 解析", AdapterNameResolved);
        TestRunner.Add("forwarded-config/没有 resolver 时 adapter 规则拒绝", AdapterWithoutResolver);
        TestRunner.Add("forwarded-config/adapter 解析失败带路径", AdapterResolveFailure);
        TestRunner.Add("forwarded-config/adapterId 与 adapterName 冲突", AdapterConflict);
        TestRunner.Add("forwarded-config/适配器任意哨兵拒绝", AdapterSentinel);
        TestRunner.Add("forwarded-config/clientCidr 与 family 不一致", FamilyMismatch);
        TestRunner.Add("forwarded-config/clientCidr 与 remoteCidr 族不同", NetworkFamilyMismatch);
        TestRunner.Add("forwarded-config/remotePorts 区间颠倒", ReversedPorts);
        TestRunner.Add("forwarded-config/forwarded 兜底不允许 redirect", FallbackRedirect);
        TestRunner.Add("forwarded-config/forwarded 规则未知字段带路径", UnknownField);
        TestRunner.Add("forwarded-config/socks5Servers 必须是数组", ServersNotArray);
        TestRunner.Add("forwarded-config/socks5Servers 缺少 name", ServerMissingName);
        TestRunner.Add("forwarded-config/socks5Servers 端口越界", ServerBadPort);
        TestRunner.Add("forwarded-config/forwarded.rules 必须是数组", RulesNotArray);
        TestRunner.Add("forwarded-config/forwarded 必须是对象", ForwardedNotObject);
        TestRunner.Add("forwarded-config/同一入口两个 clientCidr 分开编号", ClientCidrRules);
        TestRunner.Add("forwarded-config/旧 socks5 的 default 排在上游最后", LegacySocks5AppendedLast);
        TestRunner.Add("forwarded-config/controller-forwarded 报告格式", ControllerForwardedReport);
        TestRunner.Add("forwarded-config/controller-forwarded-installed 报告格式", ControllerForwardedInstalledReport);
        TestRunner.Add("forwarded-config/socks5Servers 名字不能为空", ServerEmptyName);
        TestRunner.Add("forwarded-config/remotePorts 越界", RemotePortOutOfRange);
        TestRunner.Add("forwarded-config/编译结果符合 B1 布局", CompilesToBlob);
        TestRunner.Add("forwarded-config/redirect 规则在打开设备前被拒绝", RedirectRejectedEarly);
    }

    private static ControllerConfiguration Parse(string json, IAdapterIdResolver? resolver = null) =>
        PolicyConfigurationDocument.ParseConfiguration(json, resolver);

    private static void Throws(string json, string fragment, string what, IAdapterIdResolver? resolver = null) =>
        Check.ThrowsContaining<ConfigurationException>(() => Parse(json, resolver), fragment, what);

    private static void MinimalConfig()
    {
        ControllerConfiguration configuration = Parse(MinimalJson, FakeAdapterIdResolver.Default);
        ForwardedPolicyTable table = configuration.Forwarded ?? throw new TestFailure("forwarded 表不能为 null");
        ForwardedPolicyRule rule = table.Rules[0];

        Check.Equal(1, table.Rules.Count, "forwarded 规则数");
        Check.Equal(RuleAction.Block, table.FallbackAction, "forwarded 兜底动作");
        Check.Equal("vm-tcp", rule.Name ?? "null", "规则名");
        Check.Equal(7U, rule.IngressAdapterId ?? 0U, "adapterName 解析结果");
        Check.Equal("10.0.0.0/24", rule.ClientNetwork?.ToString() ?? "null", "clientCidr");
        Check.Equal("203.0.113.0/24", rule.RemoteNetwork?.ToString() ?? "null", "remoteCidr");
        Check.Equal((ushort)443, rule.RemotePorts.Begin, "端口");
        Check.Equal(RuleProtocol.Tcp, rule.Protocol, "协议");
        Check.Equal(RuleAddressFamily.Ipv4, rule.Family, "族");
        Check.Equal(RuleAction.Redirect, rule.Action, "动作");
        Check.Equal(0U, rule.UpstreamId ?? uint.MaxValue, "target 解析成上游 id");
        Check.Equal(1, configuration.Upstreams.Count, "上游数");
        Check.Equal("127.0.0.1", configuration.Upstreams["main"].Host, "上游主机");
        Check.Equal(1080, configuration.Upstreams["main"].Port, "上游端口");
        Check.Equal(1, configuration.ForwardedUpstreams.Count, "有序上游数");
        Check.Equal(0U, configuration.ForwardedUpstreams[0].Id, "上游 id");
        Check.Equal(configuration.Upstreams["main"], configuration.ForwardedUpstreams[0].Endpoint, "有序表与名字字典同源");
    }

    private static void Defaults()
    {
        ControllerConfiguration configuration = Parse(
            """{ "fallbackAction": "permit", "forwarded": { "rules": [ { "action": "permit" } ] } }""");
        ForwardedPolicyTable table = configuration.Forwarded ?? throw new TestFailure("forwarded 表不能为 null");
        ForwardedPolicyRule rule = table.Rules[0];

        Check.Equal(RuleAction.Permit, table.FallbackAction, "forwarded 兜底默认 permit");
        Check.Equal(RuleProtocol.Any, rule.Protocol, "协议默认任意");
        Check.Equal(RuleAddressFamily.Any, rule.Family, "族默认任意");
        Check.True(rule.RemotePorts.IsAny, "端口默认任意");
        Check.True(rule.ClientNetwork is null, "clientCidr 默认没有");
        Check.True(rule.RemoteNetwork is null, "remoteCidr 默认没有");
        Check.True(rule.IngressAdapterId is null, "adapter 默认没有");
        Check.True(rule.UpstreamId is null, "上游默认没有");
        Check.Equal(0, configuration.Upstreams.Count, "没有 socks5 时上游表为空");
        Check.Equal(0, configuration.ForwardedUpstreams.Count, "没有上游时有序表为空");
        byte[] blob = ForwardedBlobCompiler.Compile(table);
        Check.Equal(0xFFFFFFFFU, ReadRule(blob, 0).UpstreamId, "空上游表写无上游哨兵");
    }

    private static void MultipleUpstreams()
    {
        ControllerConfiguration configuration = Parse("""
            {
              "fallbackAction": "permit",
              "socks5Servers": [
                { "name": "main", "host": "127.0.0.1", "port": 1080 },
                { "name": "backup", "host": "127.0.0.2", "port": 1081 }
              ],
              "forwarded": {
                "rules": [
                  { "action": "redirect", "target": "MAIN" },
                  { "action": "redirect", "target": "backup" }
                ]
              }
            }
            """);
        ForwardedPolicyTable table = configuration.Forwarded ?? throw new TestFailure("forwarded 表不能为 null");

        Check.Equal(2, configuration.Upstreams.Count, "上游数");
        Check.Equal("127.0.0.1", configuration.Upstreams["MAIN"].Host, "键大小写不敏感");
        Check.Equal(1081, configuration.Upstreams["backup"].Port, "命名上游");
        Check.Equal(0U, table.Rules[0].UpstreamId ?? uint.MaxValue, "main 是 0");
        Check.Equal(1U, table.Rules[1].UpstreamId ?? uint.MaxValue, "backup 是 1");
    }

    private static void LegacySocks5AsDefault()
    {
        ControllerConfiguration configuration = Parse("""
            {
              "fallbackAction": "permit",
              "socks5": { "host": "127.0.0.1", "port": 1080 },
              "forwarded": { "rules": [ { "action": "redirect", "target": "DEFAULT" } ] }
            }
            """);
        ForwardedPolicyTable table = configuration.Forwarded ?? throw new TestFailure("forwarded 表不能为 null");

        Check.Equal(1, configuration.Upstreams.Count, "旧 socks5 只贡献一个上游");
        Check.Equal(1080, configuration.Upstreams["default"].Port, "default 指向旧 socks5");
        Check.Equal(0U, table.Rules[0].UpstreamId ?? uint.MaxValue, "target 大小写不敏感");
    }

    private static void LegacyConfig()
    {
        ControllerConfiguration configuration = Parse("""
            { "fallbackAction": "permit", "socks5": { "host": "127.0.0.1", "port": 1080 }, "rules": [ { "action": "permit" } ] }
            """);

        Check.True(configuration.Forwarded is null, "旧配置没有 forwarded 段");
        Check.Equal(1, configuration.Upstreams.Count, "旧 socks5 仍进入上游表");
        Check.Equal(1, configuration.Policy.Rules.Count, "host 规则不受影响");
    }

    private static void ProcessRejected() =>
        Throws("""
            { "fallbackAction": "permit", "forwarded": { "rules": [ { "action": "permit", "process": "C:/app.exe" } ] } }
            """,
            "forwarded.rules[0].process", "forwarded 规则出现 process 直接拒绝");

    private static void RedirectWithoutTarget() =>
        Throws("""
            { "fallbackAction": "permit", "forwarded": { "rules": [ { "name": "vm", "action": "redirect" } ] } }
            """,
            "forwarded.rules[0].target", "redirect 缺少 target");

    private static void PermitWithTarget() =>
        Throws("""
            { "fallbackAction": "permit", "forwarded": { "rules": [ { "action": "permit", "target": "x" } ] } }
            """,
            "forwarded.rules[0].target", "permit 带 target 拒绝");

    private static void UnknownTarget() =>
        Throws("""
            { "fallbackAction": "permit", "forwarded": { "rules": [ { "action": "redirect", "target": "missing" } ] } }
            """,
            "forwarded.rules[0]", "target 不存在带规则路径");

    private static void DuplicateUpstream() =>
        Throws("""
            {
              "fallbackAction": "permit",
              "socks5Servers": [
                { "name": "Main", "host": "127.0.0.1", "port": 1080 },
                { "name": "main", "host": "127.0.0.2", "port": 1081 }
              ]
            }
            """,
            "socks5Servers[1].name", "上游名字大小写不敏感且唯一");

    private static void LegacyDefaultConflict() =>
        Throws("""
            {
              "fallbackAction": "permit",
              "socks5": { "host": "127.0.0.1", "port": 1080 },
              "socks5Servers": [ { "name": "default", "host": "127.0.0.2", "port": 1081 } ]
            }
            """,
            "socks5 段与 socks5Servers 冲突", "旧 socks5 与显式 default 冲突");

    private static void AdapterNameResolved()
    {
        ControllerConfiguration configuration = Parse("""
            { "fallbackAction": "permit", "forwarded": { "rules": [ { "action": "permit", "adapterId": "adapter-7", "adapterName": "vEthernet (MyVM)" } ] } }
            """,
            FakeAdapterIdResolver.Default);

        Check.Equal(7U, configuration.Forwarded?.Rules[0].IngressAdapterId ?? 0U, "两个选择器一致时写入同一个 id");
    }

    private static void AdapterWithoutResolver() =>
        Throws(MinimalJson, "forwarded.rules[0] 带 adapterId/adapterName，但配置解析没有提供 IAdapterIdResolver", "有 adapter 但没有 resolver");

    private static void AdapterResolveFailure() =>
        Throws("""
            { "fallbackAction": "permit", "forwarded": { "rules": [ { "action": "permit", "adapterName": "missing" } ] } }
            """,
            "forwarded.rules[0].adapterName", "adapterName 解析失败", FakeAdapterIdResolver.Default);

    private static void AdapterConflict() =>
        Throws("""
            { "fallbackAction": "permit", "forwarded": { "rules": [ { "action": "permit", "adapterId": "adapter-7", "adapterName": "vEthernet (Other)" } ] } }
            """,
            "forwarded.rules[0].adapterId 与 adapterName 解析结果不一致", "adapterId 与 adapterName 解析结果冲突", FakeAdapterIdResolver.Default);

    private static void AdapterSentinel() =>
        Throws("""
            { "fallbackAction": "permit", "forwarded": { "rules": [ { "action": "permit", "adapterName": "any-adapter" } ] } }
            """,
            "forwarded.rules[0] 的适配器解析结果是 0xFFFFFFFF", "适配器不能解析成任意入口哨兵", FakeAdapterIdResolver.Default);

    private static void FamilyMismatch() =>
        Throws("""
            { "fallbackAction": "permit", "forwarded": { "rules": [ { "action": "permit", "family": "ipv4", "clientCidr": "2001:db8::/32" } ] } }
            """,
            "forwarded.rules[0].clientCidr", "clientCidr 族与 family 不一致");

    private static void NetworkFamilyMismatch() =>
        Throws("""
            { "fallbackAction": "permit", "forwarded": { "rules": [ { "action": "permit", "clientCidr": "10.0.0.0/8", "remoteCidr": "2001:db8::/32" } ] } }
            """,
            "forwarded.rules[0] 的 clientCidr 族 4 与 remoteCidr 族 6", "两个网段族不同");

    private static void ReversedPorts() =>
        Throws("""
            { "fallbackAction": "permit", "forwarded": { "rules": [ { "action": "permit", "remotePorts": "9000-8000" } ] } }
            """,
            "forwarded.rules[0].remotePorts", "forwarded 端口区间颠倒");

    private static void FallbackRedirect() =>
        Throws("""
            { "fallbackAction": "permit", "forwarded": { "fallbackAction": "redirect" } }
            """,
            "forwarded.fallbackAction", "forwarded 兜底不支持 redirect");

    private static void UnknownField() =>
        Throws("""
            { "fallbackAction": "permit", "forwarded": { "rules": [ { "action": "permit", "remoteNetwork": "10.0.0.0/8" } ] } }
            """,
            "forwarded.rules[0].remoteNetwork", "forwarded 规则未知字段带路径");

    private static void ServersNotArray() =>
        Throws("""{ "fallbackAction": "permit", "socks5Servers": {} }""",
               "socks5Servers", "socks5Servers 必须是数组");

    private static void ServerMissingName() =>
        Throws("""{ "fallbackAction": "permit", "socks5Servers": [ { "host": "127.0.0.1", "port": 1080 } ] }""",
               "socks5Servers[0].name", "上游缺少 name");

    private static void ServerBadPort() =>
        Throws("""{ "fallbackAction": "permit", "socks5Servers": [ { "name": "main", "host": "127.0.0.1", "port": 0 } ] }""",
               "socks5Servers[0].port", "上游端口越界");

    private static void RulesNotArray() =>
        Throws("""{ "fallbackAction": "permit", "forwarded": { "rules": 1 } }""",
               "forwarded.rules", "forwarded.rules 必须是数组");

    private static void ForwardedNotObject() =>
        Throws("""{ "fallbackAction": "permit", "forwarded": [] }""",
               "forwarded 必须是对象", "forwarded 必须是对象");

    private static void CompilesToBlob()
    {
        ForwardedPolicyTable table = Parse(MinimalJson, FakeAdapterIdResolver.Default).Forwarded
                                     ?? throw new TestFailure("forwarded 表不能为 null");
        byte[] blob = ForwardedBlobCompiler.Compile(table);
        ForwardedBlobHeader header = MemoryMarshal.Read<ForwardedBlobHeader>(blob);
        ForwardedBlobRule rule = ReadRule(blob, 0);
        RuleBlobNetworkPrefix clientNetwork = ReadNetwork(blob, 0);
        RuleBlobNetworkPrefix remoteNetwork = ReadNetwork(blob, 1);

        Check.Equal(1U, header.RuleCount, "header rule_count");
        Check.Equal(1U, header.FallbackAction, "header fallback_action");
        Check.Equal(40U, header.NetworkPoolSize, "网段池有两条记录");
        Check.Equal(7U, rule.IngressAdapterId, "入口适配器");
        Check.Equal(0U, rule.ClientNetworkIndex, "client 网段指向池首");
        Check.Equal(20U, rule.RemoteNetworkIndex, "remote 网段指向第二条记录");
        Check.Equal(443, (int)rule.RemotePortStart, "端口起点");
        Check.Equal(443, (int)rule.RemotePortEnd, "端口终点");
        Check.Equal(0U, rule.UpstreamId, "上游 id");
        Check.Equal((byte)6, rule.Protocol, "协议字节");
        Check.Equal((byte)4, rule.Family, "族字节");
        Check.Equal((byte)2, rule.Action, "动作字节");
        Check.Equal((byte)24, clientNetwork.PrefixLength, "client 前缀长度");
        Check.Equal((byte)24, remoteNetwork.PrefixLength, "remote 前缀长度");
    }

    private static void ClientCidrRules()
    {
        ControllerConfiguration configuration = Parse("""
            {
              "fallbackAction": "permit",
              "forwarded": {
                "rules": [
                  { "name": "first", "adapterName": "vEthernet (MyVM)", "clientCidr": "10.0.0.0/24", "action": "permit" },
                  { "name": "second", "adapterName": "vEthernet (MyVM)", "clientCidr": "10.0.1.0/24", "action": "permit" }
                ]
              }
            }
            """,
            FakeAdapterIdResolver.Default);
        ForwardedPolicyTable table = configuration.Forwarded ?? throw new TestFailure("forwarded 表不能为 null");
        byte[] blob = ForwardedBlobCompiler.Compile(table);
        ForwardedBlobHeader header = MemoryMarshal.Read<ForwardedBlobHeader>(blob);
        ForwardedBlobRule first = ReadRule(blob, 0);
        ForwardedBlobRule second = ReadRule(blob, 1);
        RuleBlobNetworkPrefix firstClient = ReadNetwork(blob, 0);
        RuleBlobNetworkPrefix secondClient = ReadNetwork(blob, 1);

        Check.Equal(2U, header.RuleCount, "两条 clientCidr 规则");
        Check.Equal(7U, first.IngressAdapterId, "同一入口：第一条");
        Check.Equal(7U, second.IngressAdapterId, "同一入口：第二条");
        Check.Equal(0U, first.ClientNetworkIndex, "第一条 client 网段在池首");
        Check.Equal(20U, second.ClientNetworkIndex, "第二条 client 网段不共享索引");
        Check.Equal((byte)24, firstClient.PrefixLength, "第一条前缀长度");
        Check.Equal((byte)24, secondClient.PrefixLength, "第二条前缀长度");
        Check.Equal((byte)0, firstClient.Network[3], "10.0.0.0/24 的主机位清零");
        Check.Equal((byte)1, secondClient.Network[2], "10.0.1.0/24 的第三字节");
    }

    private static void LegacySocks5AppendedLast()
    {
        ControllerConfiguration configuration = Parse("""
            {
              "fallbackAction": "permit",
              "socks5": { "host": "127.0.0.3", "port": 1082 },
              "socks5Servers": [
                { "name": "main", "host": "127.0.0.1", "port": 1080 },
                { "name": "backup", "host": "127.0.0.2", "port": 1081 }
              ],
              "forwarded": {
                "rules": [
                  { "action": "redirect", "target": "main" },
                  { "action": "redirect", "target": "backup" },
                  { "action": "redirect", "target": "default" }
                ]
              }
            }
            """);
        ForwardedPolicyTable table = configuration.Forwarded ?? throw new TestFailure("forwarded 表不能为 null");
        byte[] blob = ForwardedBlobCompiler.Compile(table);

        List<ForwardedUpstream> ordered = configuration.ForwardedUpstreams.ToList();
        Check.Equal(3, ordered.Count, "有序上游数");
        Check.Equal(0U, ordered[0].Id, "main 的 id");
        Check.Equal("main", ordered[0].Name, "第 0 项");
        Check.Equal("backup", ordered[1].Name, "第 1 项");
        Check.Equal(2U, ordered[2].Id, "default 的 id");
        Check.Equal("default", ordered[2].Name, "default 在最后");
        Check.Equal(1082, ordered[2].Endpoint.Port, "default endpoint");
        Check.Equal(configuration.Upstreams["backup"], ordered[1].Endpoint, "名字字典与有序表同源");
        for (int index = 0; index < ordered.Count; index++)
        {
            Check.Equal(ordered[index].Id, table.Rules[index].UpstreamId ?? uint.MaxValue, $"规则 {index} 的 id");
            Check.Equal(ordered[index].Id, ReadRule(blob, index).UpstreamId, $"blob 规则 {index} 的 id");
        }

        Check.Equal(3, configuration.Upstreams.Count, "三个上游");
        Check.Equal(1082, configuration.Upstreams["default"].Port, "default 指向旧 socks5");
        Check.Equal(0U, table.Rules[0].UpstreamId ?? uint.MaxValue, "main 是 0");
        Check.Equal(1U, table.Rules[1].UpstreamId ?? uint.MaxValue, "backup 是 1");
        Check.Equal(2U, table.Rules[2].UpstreamId ?? uint.MaxValue, "default 排最后，是 2");
        Check.Equal(2U, ReadRule(blob, 2).UpstreamId, "blob 里 default 也是 2");
    }

    private static void ServerEmptyName() =>
        Throws("""{ "fallbackAction": "permit", "socks5Servers": [ { "name": "   ", "host": "127.0.0.1", "port": 1080 } ] }""",
               "socks5Servers[0].name", "上游 name 不能为空");

    private static void RemotePortOutOfRange() =>
        Throws("""{ "fallbackAction": "permit", "forwarded": { "rules": [ { "action": "permit", "remotePorts": "65536" } ] } }""",
               "forwarded.rules[0].remotePorts", "forwarded remotePorts 越界");

    private static void ControllerForwardedReport()
    {
        Check.Equal("controller-forwarded count=1 upstreams=0 blob=64",
                    ForwardedReport(Parse(
                        """{ "fallbackAction": "permit", "forwarded": { "rules": [ { "action": "permit" } ] } }""")),
                    "0 上游");

        Check.Equal("controller-forwarded count=1 upstreams=1 blob=104",
                    ForwardedReport(Parse(MinimalJson, FakeAdapterIdResolver.Default)),
                    "1 上游");

        Check.Equal("controller-forwarded count=2 upstreams=2 blob=88",
                    ForwardedReport(Parse(
                        """{ "fallbackAction": "permit", "socks5Servers": [ { "name": "main", "host": "127.0.0.1", "port": 1080 }, { "name": "backup", "host": "127.0.0.2", "port": 1081 } ], "forwarded": { "rules": [ { "action": "redirect", "target": "main" }, { "action": "redirect", "target": "backup" } ] } }""")),
                    "多上游");
    }

    private static void ControllerForwardedInstalledReport()
    {
        Check.Equal("controller-forwarded-installed generation=7 count=2 fallback=1 blob=640",
                    ControllerReport.ForwardedInstalled(7, 2, 1, 640),
                    "回读报告");
    }

    private static void RedirectRejectedEarly()
    {
        ControllerConfiguration configuration = Parse(
            """{ "fallbackAction": "permit", "socks5Servers": [ { "name": "main", "host": "127.0.0.1", "port": 1080 } ], "forwarded": { "rules": [ { "name": "web", "action": "redirect", "target": "main" } ] } }""");
        Check.ThrowsContaining<ConfigurationException>(
            () => ForwardedRuntimeSupport.EnsureExecutable(configuration.Forwarded),
            "B4b",
            "redirect 在打开设备前被拒绝并指出 B4b");
    }
    private static string ForwardedReport(ControllerConfiguration configuration)
    {
        ForwardedPolicyTable table = configuration.Forwarded ?? throw new TestFailure("forwarded 表不能为 null");
        return ControllerReport.Forwarded(
            table.Rules.Count,
            configuration.ForwardedUpstreams.Count,
            ForwardedBlobCompiler.Compile(table).Length);
    }

    private static ForwardedBlobRule ReadRule(byte[] blob, int index)
    {
        ForwardedBlobHeader header = MemoryMarshal.Read<ForwardedBlobHeader>(blob);
        int offset = (int)header.RulesOffset + (index * ForwardedBlobLayout.RuleSize);
        return MemoryMarshal.Read<ForwardedBlobRule>(blob.AsSpan(offset, ForwardedBlobLayout.RuleSize));
    }

    private static RuleBlobNetworkPrefix ReadNetwork(byte[] blob, int index)
    {
        ForwardedBlobHeader header = MemoryMarshal.Read<ForwardedBlobHeader>(blob);
        int offset = (int)header.NetworkPoolOffset + (index * ForwardedBlobLayout.NetworkPrefixSize);
        return MemoryMarshal.Read<RuleBlobNetworkPrefix>(blob.AsSpan(offset, ForwardedBlobLayout.NetworkPrefixSize));
    }
}

/// <summary>测试用适配器解析表：名字与 id 都映射到固定接口编号。</summary>
internal sealed class FakeAdapterIdResolver : IAdapterIdResolver
{
    internal static FakeAdapterIdResolver Default { get; } = new();

    private static readonly Dictionary<string, uint> ById = new(StringComparer.Ordinal)
    {
        ["adapter-7"] = 7,
        ["adapter-9"] = 9,
    };

    private static readonly Dictionary<string, uint> ByName = new(StringComparer.Ordinal)
    {
        ["vEthernet (MyVM)"] = 7,
        ["vEthernet (Other)"] = 9,
        ["any-adapter"] = 0xFFFFFFFFU,
    };

    public bool TryResolve(string? adapterId, string? adapterName, out uint id, out string error)
    {
        if (adapterId is not null && adapterName is not null)
        {
            id = 0;
            error = "一次只解析一个选择器";
            return false;
        }

        if (adapterId is not null)
        {
            return TryLookup(ById, adapterId, "id", out id, out error);
        }

        if (adapterName is not null)
        {
            return TryLookup(ByName, adapterName, "name", out id, out error);
        }

        id = 0;
        error = "没有提供 adapterId 或 adapterName";
        return false;
    }

    private static bool TryLookup(Dictionary<string, uint> map,
                                  string key,
                                  string kind,
                                  out uint id,
                                  out string error)
    {
        if (map.TryGetValue(key, out id))
        {
            error = string.Empty;
            return true;
        }

        error = $"没有名为「{key}」的适配器 {kind}";
        return false;
    }
}
