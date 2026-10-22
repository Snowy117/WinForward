using System.Runtime.InteropServices;
using WinForward.Userland.Forwarded;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class ForwardedCompilerTests
{
    internal static void Register()
    {
        TestRunner.Add("forwarded-compiler/空表只产出 40 字节头部", EmptyTable);
        TestRunner.Add("forwarded-compiler/各段起点 4 字节对齐且不重叠", Segments);
        TestRunner.Add("forwarded-compiler/规则字段原样写入", RuleFields);
        TestRunner.Add("forwarded-compiler/任意入口与无上游写成哨兵", Sentinels);
        TestRunner.Add("forwarded-compiler/0 是合法的具体适配器与上游编号", ZeroIsConcrete);
        TestRunner.Add("forwarded-compiler/网段索引是池内字节偏移", NetworkIndexes);
        TestRunner.Add("forwarded-compiler/网段去重且按首次出现排序", NetworkDedupAndOrder);
        TestRunner.Add("forwarded-compiler/IPv4 地址只占前 4 字节", NetworkIpv4Padding);
        TestRunner.Add("forwarded-compiler/UDP 协议写成 17", UdpProtocol);
        TestRunner.Add("forwarded-compiler/兜底动作写入头部", FallbackAction);
        TestRunner.Add("forwarded-compiler/规则与头部保留位为 0", ReservedIsZero);
        TestRunner.Add("forwarded-compiler/规则数上限含端", RuleLimitIsInclusive);
        TestRunner.Add("forwarded-compiler/网段池上限含端", NetworkLimitIsInclusive);
        TestRunner.Add("forwarded-invalid/空表为 null", NullTable);
        TestRunner.Add("forwarded-invalid/规则为 null", NullRule);
        TestRunner.Add("forwarded-invalid/兜底动作不允许 redirect", FallbackRedirect);
        TestRunner.Add("forwarded-invalid/兜底动作未知", UnknownFallback);
        TestRunner.Add("forwarded-invalid/动作未知", UnknownAction);
        TestRunner.Add("forwarded-invalid/协议未知", UnknownProtocol);
        TestRunner.Add("forwarded-invalid/地址族未知", UnknownFamily);
        TestRunner.Add("forwarded-invalid/端口区间颠倒", InvertedPorts);
        TestRunner.Add("forwarded-invalid/入口适配器不能是任意哨兵", AdapterSentinel);
        TestRunner.Add("forwarded-invalid/redirect 缺少上游", RedirectWithoutUpstream);
        TestRunner.Add("forwarded-invalid/redirect 上游不能是无上游哨兵", RedirectUpstreamNone);
        TestRunner.Add("forwarded-invalid/非 redirect 不得带上游", NonRedirectWithUpstream);
        TestRunner.Add("forwarded-invalid/规则族与 clientCidr 不一致", FamilyMismatchClient);
        TestRunner.Add("forwarded-invalid/规则族与 remoteCidr 不一致", FamilyMismatchRemote);
        TestRunner.Add("forwarded-invalid/两个网段族不同", NetworkFamilyMismatch);
        TestRunner.Add("forwarded-invalid/规则数超过上限", RuleLimit);
        TestRunner.Add("forwarded-invalid/网段池 4097 条拒绝", NetworkLimit);
        TestRunner.Add("forwarded-invalid/错误消息带规则序号", ErrorIncludesRuleIndex);
    }

    private static ForwardedBlobHeader Header(byte[] blob) => MemoryMarshal.Read<ForwardedBlobHeader>(blob);

    private static ForwardedBlobRule Rule(byte[] blob, int index)
    {
        ForwardedBlobHeader header = Header(blob);
        int offset = (int)header.RulesOffset + (index * ForwardedBlobLayout.RuleSize);
        return MemoryMarshal.Read<ForwardedBlobRule>(blob.AsSpan(offset, ForwardedBlobLayout.RuleSize));
    }

    private static RuleBlobNetworkPrefix NetworkPrefix(byte[] blob, int index)
    {
        ForwardedBlobHeader header = Header(blob);
        int offset = (int)header.NetworkPoolOffset + (index * ForwardedBlobLayout.NetworkPrefixSize);
        return MemoryMarshal.Read<RuleBlobNetworkPrefix>(blob.AsSpan(offset, ForwardedBlobLayout.NetworkPrefixSize));
    }

    private static byte[] Compile(params ForwardedPolicyRule[] rules) =>
        ForwardedBlobCompiler.Compile(new ForwardedPolicyTable(rules, RuleAction.Permit));

    private static ForwardedPolicyTable SampleTable() => new(
        new[]
        {
            new ForwardedPolicyRule
            {
                IngressAdapterId = 7,
                ClientNetwork = RemoteNetwork.Parse("10.1.2.3/24"),
                RemoteNetwork = RemoteNetwork.Parse("203.0.113.9/24"),
                RemotePorts = new PortRange(443, 8443),
                Protocol = RuleProtocol.Tcp,
                Family = RuleAddressFamily.Ipv4,
                Action = RuleAction.Redirect,
                UpstreamId = 5,
            },
            new ForwardedPolicyRule { Action = RuleAction.Block },
        },
        RuleAction.Block);

    private static RemoteNetwork NetworkAt(int index) =>
        RemoteNetwork.Parse($"10.{index / 256}.{index % 256}.0/24");

    private static void EmptyTable()
    {
        byte[] blob = Compile();
        ForwardedBlobHeader header = Header(blob);

        Check.Equal(40, blob.Length, "空表 blob 长度");
        Check.Equal(0x31424657U, header.Magic, "magic");
        Check.Equal((ushort)1, header.Version, "version");
        Check.Equal((ushort)40, header.HeaderSize, "header_size");
        Check.Equal(40U, header.TotalSize, "total_size");
        Check.Equal(0U, header.RuleCount, "rule_count");
        Check.Equal(0U, header.FallbackAction, "fallback_action");
        Check.Equal(40U, header.RulesOffset, "rules_offset");
        Check.Equal(40U, header.NetworkPoolOffset, "network_pool_offset");
        Check.Equal(0U, header.NetworkPoolSize, "network_pool_size");
        Check.Equal(0U, header.Reserved, "reserved");
        Check.Equal(0U, header.Reserved2, "reserved2");
    }

    private static void Segments()
    {
        byte[] blob = ForwardedBlobCompiler.Compile(SampleTable());
        ForwardedBlobHeader header = Header(blob);
        int rulesEnd = (int)header.RulesOffset + ((int)header.RuleCount * ForwardedBlobLayout.RuleSize);
        int networkEnd = (int)header.NetworkPoolOffset + (int)header.NetworkPoolSize;

        Check.Equal(blob.Length, (int)header.TotalSize, "total_size 等于实际长度");
        Check.Equal(0, (int)header.RulesOffset % 4, "rules_offset 对齐");
        Check.Equal(0, (int)header.NetworkPoolOffset % 4, "network_pool_offset 对齐");
        Check.Equal(40, (int)header.RulesOffset, "rules_offset 紧接头部");
        Check.True(rulesEnd <= (int)header.NetworkPoolOffset, "规则数组不越过网段池");
        Check.Equal(blob.Length, networkEnd, "网段池结束于 blob 末尾");
        Check.True((int)header.NetworkPoolOffset - rulesEnd < 4, "规则数组与网段池之间只有对齐填充");
    }

    private static void RuleFields()
    {
        byte[] blob = ForwardedBlobCompiler.Compile(SampleTable());
        ForwardedBlobRule first = Rule(blob, 0);
        ForwardedBlobRule second = Rule(blob, 1);

        Check.Equal(7U, first.IngressAdapterId, "规则 0 入口适配器");
        Check.Equal(0U, first.ClientNetworkIndex, "规则 0 客户端网段指向池首");
        Check.Equal(20U, first.RemoteNetworkIndex, "规则 0 目的网段指向第二条记录");
        Check.Equal(443, (int)first.RemotePortStart, "规则 0 端口起点");
        Check.Equal(8443, (int)first.RemotePortEnd, "规则 0 端口终点");
        Check.Equal(5U, first.UpstreamId, "规则 0 上游");
        Check.Equal((byte)6, first.Protocol, "规则 0 协议");
        Check.Equal((byte)4, first.Family, "规则 0 地址族");
        Check.Equal((byte)2, first.Action, "规则 0 动作");
        Check.Equal((byte)1, second.Action, "规则 1 动作");
    }

    private static void Sentinels()
    {
        byte[] blob = Compile(new ForwardedPolicyRule());
        ForwardedBlobRule rule = Rule(blob, 0);

        Check.Equal(0xFFFFFFFFU, rule.IngressAdapterId, "入口适配器任意");
        Check.Equal(0xFFFFFFFFU, rule.ClientNetworkIndex, "客户端网段不限");
        Check.Equal(0xFFFFFFFFU, rule.RemoteNetworkIndex, "目的网段不限");
        Check.Equal(0xFFFFFFFFU, rule.UpstreamId, "无上游");
        Check.Equal(0, (int)rule.RemotePortStart, "端口起点 0");
        Check.Equal(0xFFFF, (int)rule.RemotePortEnd, "端口终点 65535");
    }

    private static void ZeroIsConcrete()
    {
        byte[] blob = Compile(new ForwardedPolicyRule
        {
            IngressAdapterId = 0,
            Action = RuleAction.Redirect,
            UpstreamId = 0,
        });
        ForwardedBlobRule rule = Rule(blob, 0);

        Check.Equal(0U, rule.IngressAdapterId, "适配器 0 原样写入，不当成任意哨兵");
        Check.Equal(0U, rule.UpstreamId, "上游 0 原样写入，不当成无上游哨兵");
    }

    private static void NetworkIndexes()
    {
        byte[] blob = Compile(
            new ForwardedPolicyRule { RemoteNetwork = RemoteNetwork.Parse("10.0.0.0/8") },
            new ForwardedPolicyRule { RemoteNetwork = RemoteNetwork.Parse("192.168.0.0/16") },
            new ForwardedPolicyRule
            {
                RemoteNetwork = RemoteNetwork.Parse("2001:db8::/32"),
                Family = RuleAddressFamily.Ipv6,
            });

        Check.Equal(0U, Rule(blob, 0).RemoteNetworkIndex, "规则 0");
        Check.Equal(20U, Rule(blob, 1).RemoteNetworkIndex, "规则 1");
        Check.Equal(40U, Rule(blob, 2).RemoteNetworkIndex, "规则 2");
        Check.Equal(0, (int)Rule(blob, 2).RemoteNetworkIndex % 20, "网段索引是 20 的倍数");
    }

    private static void NetworkDedupAndOrder()
    {
        byte[] blob = Compile(
            new ForwardedPolicyRule { RemoteNetwork = RemoteNetwork.Parse("10.1.9.77/24") },
            new ForwardedPolicyRule { RemoteNetwork = RemoteNetwork.Parse("192.168.1.0/16") },
            new ForwardedPolicyRule { RemoteNetwork = RemoteNetwork.Parse("10.1.9.1/24") });

        Check.Equal(40U, Header(blob).NetworkPoolSize, "同网段的不同写法去重后只剩两条记录");
        Check.Equal(0U, Rule(blob, 0).RemoteNetworkIndex, "规则 0 指向池首");
        Check.Equal(20U, Rule(blob, 1).RemoteNetworkIndex, "规则 1 指向第二条记录");
        Check.Equal(0U, Rule(blob, 2).RemoteNetworkIndex, "规则 2 复用第一条记录");
        Check.Equal((byte)24, NetworkPrefix(blob, 0).PrefixLength, "首条记录是 10.1.9.0/24");
        Check.BytesEqual(new byte[] { 10, 1, 9, 0 },
                           BlobAddress(NetworkPrefix(blob, 0)).AsSpan(0, 4),
                           "首次出现的网段排在池首");
    }

    private static void NetworkIpv4Padding()
    {
        byte[] blob = Compile(new ForwardedPolicyRule { RemoteNetwork = RemoteNetwork.Parse("192.168.1.9/24") });
        RuleBlobNetworkPrefix prefix = NetworkPrefix(blob, 0);

        Check.BytesEqual(new byte[] { 192, 168, 1, 0 }, BlobAddress(prefix).AsSpan(0, 4), "IPv4 地址且主机位清零");
        Check.BytesEqual(new byte[12], BlobAddress(prefix).AsSpan(4, 12), "IPv4 的后 12 字节补零");
    }

    private static void UdpProtocol()
    {
        byte[] blob = Compile(new ForwardedPolicyRule { Protocol = RuleProtocol.Udp });
        Check.Equal(17, (int)RuleProtocol.Udp, "UDP 枚举值");
        Check.Equal((byte)17, Rule(blob, 0).Protocol, "UDP 规则协议字节");
    }

    private static void FallbackAction()
    {
        byte[] blob = ForwardedBlobCompiler.Compile(
            new ForwardedPolicyTable(Array.Empty<ForwardedPolicyRule>(), RuleAction.Block));
        Check.Equal(1U, Header(blob).FallbackAction, "fallback_action 写进头部");
    }

    private static void ReservedIsZero()
    {
        byte[] blob = ForwardedBlobCompiler.Compile(SampleTable());
        ForwardedBlobHeader header = Header(blob);
        Check.Equal(0U, header.Reserved, "头部 reserved");
        Check.Equal(0U, header.Reserved2, "头部 reserved2");
        for (int index = 0; index < (int)header.RuleCount; index++)
        {
            Check.Equal((byte)0, Rule(blob, index).Reserved, $"规则 {index} 的 reserved");
        }
        int networkCount = (int)(header.NetworkPoolSize / ForwardedBlobLayout.NetworkPrefixSize);
        for (int index = 0; index < networkCount; index++)
        {
            RuleBlobNetworkPrefix prefix = NetworkPrefix(blob, index);
            Check.Equal((byte)0, prefix.Reserved0, $"网段 {index} 的 reserved[0]");
            Check.Equal((byte)0, prefix.Reserved1, $"网段 {index} 的 reserved[1]");
        }
    }

    private static void RuleLimitIsInclusive()
    {
        ForwardedPolicyRule[] rules = new ForwardedPolicyRule[ForwardedPolicyLimits.MaxRules];
        for (int index = 0; index < rules.Length; index++)
        {
            rules[index] = new ForwardedPolicyRule();
        }

        byte[] blob = Compile(rules);
        Check.Equal((uint)ForwardedPolicyLimits.MaxRules, Header(blob).RuleCount, "上限值本身可编译");
    }

    private static void NullTable() =>
        Check.Throws<ArgumentNullException>(() => ForwardedBlobCompiler.Compile(null!), "Compile(null)");

    private static void NullRule() =>
        Check.Throws<PolicyCompileException>(
            () => Compile(new ForwardedPolicyRule[] { null! }), "规则列表里出现 null");

    private static void FallbackRedirect() =>
        Check.ThrowsContaining<PolicyCompileException>(
            () => ForwardedBlobCompiler.Compile(
                new ForwardedPolicyTable(Array.Empty<ForwardedPolicyRule>(), RuleAction.Redirect)),
            "兜底动作",
            "兜底动作 redirect");

    private static void UnknownFallback() =>
        Check.Throws<PolicyCompileException>(
            () => ForwardedBlobCompiler.Compile(
                new ForwardedPolicyTable(Array.Empty<ForwardedPolicyRule>(), (RuleAction)9)),
            "兜底动作 9");

    private static void UnknownAction() =>
        Check.Throws<PolicyCompileException>(
            () => Compile(new ForwardedPolicyRule { Action = (RuleAction)7 }),
            "规则动作 7");

    private static void UnknownProtocol() =>
        Check.Throws<PolicyCompileException>(
            () => Compile(new ForwardedPolicyRule { Protocol = (RuleProtocol)18 }),
            "协议 18");

    private static void UnknownFamily() =>
        Check.Throws<PolicyCompileException>(
            () => Compile(new ForwardedPolicyRule { Family = (RuleAddressFamily)5 }),
            "地址族 5");

    private static void InvertedPorts() =>
        Check.Throws<PolicyCompileException>(
            () => Compile(new ForwardedPolicyRule { RemotePorts = new PortRange(500, 400) }),
            "端口 500..400");

    private static void AdapterSentinel() =>
        Check.Throws<PolicyCompileException>(
            () => Compile(new ForwardedPolicyRule { IngressAdapterId = 0xFFFFFFFFU }),
            "入口适配器写成任意哨兵");

    private static void RedirectWithoutUpstream() =>
        Check.Throws<PolicyCompileException>(
            () => Compile(new ForwardedPolicyRule { Action = RuleAction.Redirect }),
            "redirect 缺少上游");

    private static void RedirectUpstreamNone() =>
        Check.Throws<PolicyCompileException>(
            () => Compile(new ForwardedPolicyRule
            {
                Action = RuleAction.Redirect,
                UpstreamId = 0xFFFFFFFFU,
            }),
            "redirect 上游写成无上游哨兵");

    private static void NonRedirectWithUpstream() =>
        Check.Throws<PolicyCompileException>(
            () => Compile(new ForwardedPolicyRule { Action = RuleAction.Permit, UpstreamId = 3 }),
            "permit 带上游");

    private static void FamilyMismatchClient() =>
        Check.Throws<PolicyCompileException>(
            () => Compile(new ForwardedPolicyRule
            {
                Family = RuleAddressFamily.Ipv4,
                ClientNetwork = RemoteNetwork.Parse("2001:db8::/32"),
            }),
            "IPv4 规则配 IPv6 客户端网段");

    private static void FamilyMismatchRemote() =>
        Check.Throws<PolicyCompileException>(
            () => Compile(new ForwardedPolicyRule
            {
                Family = RuleAddressFamily.Ipv6,
                RemoteNetwork = RemoteNetwork.Parse("10.0.0.0/8"),
            }),
            "IPv6 规则配 IPv4 目的网段");

    private static void NetworkFamilyMismatch() =>
        Check.Throws<PolicyCompileException>(
            () => Compile(new ForwardedPolicyRule
            {
                ClientNetwork = RemoteNetwork.Parse("10.0.0.0/8"),
                RemoteNetwork = RemoteNetwork.Parse("2001:db8::/32"),
            }),
            "规则族 any 但两个网段族不同");

    private static void RuleLimit()
    {
        ForwardedPolicyRule[] rules = new ForwardedPolicyRule[ForwardedPolicyLimits.MaxRules + 1];
        for (int index = 0; index < rules.Length; index++)
        {
            rules[index] = new ForwardedPolicyRule();
        }

        Check.Throws<PolicyCompileException>(() => Compile(rules), "规则数超限");
    }

    private static void NetworkLimit()
    {
        ForwardedPolicyRule[] rules = new ForwardedPolicyRule[2049];
        for (int index = 0; index < 2048; index++)
        {
            rules[index] = new ForwardedPolicyRule
            {
                ClientNetwork = NetworkAt(index * 2),
                RemoteNetwork = NetworkAt((index * 2) + 1),
            };
        }

        // 2048 条规则各 2 个网段，最后一条只带 1 个：合计 4097 条，刚好超过上限。
        rules[2048] = new ForwardedPolicyRule { ClientNetwork = NetworkAt(4096) };

        Check.Throws<PolicyCompileException>(() => Compile(rules), "网段池 4097 条超过上限");
    }

    private static void NetworkLimitIsInclusive()
    {
        ForwardedPolicyRule[] rules = new ForwardedPolicyRule[2048];
        for (int index = 0; index < rules.Length; index++)
        {
            rules[index] = new ForwardedPolicyRule
            {
                ClientNetwork = NetworkAt(index * 2),
                RemoteNetwork = NetworkAt((index * 2) + 1),
            };
        }

        byte[] blob = Compile(rules);
        Check.Equal((uint)(4096 * ForwardedBlobLayout.NetworkPrefixSize),
                    Header(blob).NetworkPoolSize,
                    "网段池 4096 条（含端）可编译");
    }

    private static void ErrorIncludesRuleIndex()
    {
        Check.ThrowsContaining<PolicyCompileException>(
            () => Compile(
                new ForwardedPolicyRule(),
                new ForwardedPolicyRule { RemotePorts = new PortRange(500, 400) }),
            "规则 #1",
            "第二条规则出错时消息带规则序号");
    }

    private static byte[] BlobAddress(RuleBlobNetworkPrefix prefix)
    {
        byte[] address = new byte[ForwardedBlobLayout.NetworkAddressSize];
        for (int index = 0; index < address.Length; index++)
        {
            address[index] = prefix.Network[index];
        }

        return address;
    }
}
