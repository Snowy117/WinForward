using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinForward.Userland.Device;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class LayoutTests
{
    internal static void Register()
    {
        TestRunner.Add("layout/头部 56 字节", HeaderSize);
        TestRunner.Add("layout/头部逐字段偏移", HeaderOffsets);
        TestRunner.Add("layout/规则 32 字节", RuleSize);
        TestRunner.Add("layout/规则逐字段偏移", RuleOffsets);
        TestRunner.Add("layout/进程选择器 8 字节", SelectorSize);
        TestRunner.Add("layout/进程选择器逐字段偏移", SelectorOffsets);
        TestRunner.Add("layout/端口记录 4 字节", PortRangeSize);
        TestRunner.Add("layout/端口记录逐字段偏移", PortRangeOffsets);
        TestRunner.Add("layout/网段记录 20 字节", NetworkSize);
        TestRunner.Add("layout/网段逐字段偏移", NetworkOffsets);
        TestRunner.Add("layout/托管尺寸与 Marshal 尺寸一致", ManagedSizes);
        TestRunner.Add("layout/ABI v7 事件、统计与 forwarded 结构", AbiVersionSeven);
        TestRunner.Add("layout/头部黄金字节序列", HeaderGoldenBytes);
        TestRunner.Add("layout/blob 头部魔数在内存中是 WRB1", MagicBytes);
        TestRunner.Add("layout/部署目标是主机小端", LittleEndianHost);
    }

    private static void HeaderSize()
    {
        Check.Equal(56, Marshal.SizeOf<RuleBlobHeader>(), "Marshal.SizeOf<RuleBlobHeader>()");
        Check.Equal(56, Unsafe.SizeOf<RuleBlobHeader>(), "Unsafe.SizeOf<RuleBlobHeader>()");
        Check.Equal(4, typeof(RuleBlobHeader).StructLayoutAttribute?.Pack ?? 0, "Pack(rule_blob_header)");
    }

    private static void HeaderOffsets()
    {
        (string Name, long Offset)[] expected =
        {
            (nameof(RuleBlobHeader.Magic), 0),
            (nameof(RuleBlobHeader.Version), 4),
            (nameof(RuleBlobHeader.HeaderSize), 6),
            (nameof(RuleBlobHeader.TotalSize), 8),
            (nameof(RuleBlobHeader.RuleCount), 12),
            (nameof(RuleBlobHeader.FallbackAction), 16),
            (nameof(RuleBlobHeader.RulesOffset), 20),
            (nameof(RuleBlobHeader.ProcessPoolOffset), 24),
            (nameof(RuleBlobHeader.ProcessPoolSize), 28),
            (nameof(RuleBlobHeader.SelectorPoolOffset), 32),
            (nameof(RuleBlobHeader.SelectorPoolSize), 36),
            (nameof(RuleBlobHeader.NetworkPoolOffset), 40),
            (nameof(RuleBlobHeader.NetworkPoolSize), 44),
            (nameof(RuleBlobHeader.PortPoolOffset), 48),
            (nameof(RuleBlobHeader.PortPoolSize), 52),
        };

        foreach ((string name, long offset) in expected)
        {
            Check.Equal(offset, (long)Marshal.OffsetOf<RuleBlobHeader>(name), $"offsetof(rule_blob_header, {name})");
        }
    }

    private static void RuleSize()
    {
        Check.Equal(32, Marshal.SizeOf<RuleBlobRule>(), "Marshal.SizeOf<RuleBlobRule>()");
        Check.Equal(32, Unsafe.SizeOf<RuleBlobRule>(), "Unsafe.SizeOf<RuleBlobRule>()");
        Check.Equal(4, typeof(RuleBlobRule).StructLayoutAttribute?.Pack ?? 0, "Pack(rule)");
    }

    private static void RuleOffsets()
    {
        (string Name, long Offset)[] expected =
        {
            (nameof(RuleBlobRule.ProcessFirst), 0),
            (nameof(RuleBlobRule.ProcessCount), 4),
            (nameof(RuleBlobRule.NetworkFirst), 8),
            (nameof(RuleBlobRule.NetworkCount), 12),
            (nameof(RuleBlobRule.PortFirst), 16),
            (nameof(RuleBlobRule.PortCount), 20),
            (nameof(RuleBlobRule.ProtocolMask), 24),
            (nameof(RuleBlobRule.FamilyMask), 25),
            (nameof(RuleBlobRule.Action), 26),
            (nameof(RuleBlobRule.Reserved), 27),
            (nameof(RuleBlobRule.Reserved2), 28),
        };

        foreach ((string name, long offset) in expected)
        {
            Check.Equal(offset, (long)Marshal.OffsetOf<RuleBlobRule>(name), $"offsetof(rule, {name})");
        }
    }

    private static void SelectorSize()
    {
        Check.Equal(8, Marshal.SizeOf<RuleBlobProcessSelector>(), "Marshal.SizeOf<process_selector>()");
        Check.Equal(8, Unsafe.SizeOf<RuleBlobProcessSelector>(), "Unsafe.SizeOf<process_selector>()");
        Check.Equal(4, typeof(RuleBlobProcessSelector).StructLayoutAttribute?.Pack ?? 0, "Pack(process_selector)");
    }

    private static void SelectorOffsets()
    {
        (string Name, long Offset)[] expected =
        {
            (nameof(RuleBlobProcessSelector.StringOffset), 0),
            (nameof(RuleBlobProcessSelector.Kind), 4),
            (nameof(RuleBlobProcessSelector.IsDirectory), 5),
            (nameof(RuleBlobProcessSelector.Reserved0), 6),
            (nameof(RuleBlobProcessSelector.Reserved1), 7),
        };

        foreach ((string name, long offset) in expected)
        {
            Check.Equal(offset, (long)Marshal.OffsetOf<RuleBlobProcessSelector>(name), $"offsetof(process_selector, {name})");
        }
    }

    private static void PortRangeSize()
    {
        Check.Equal(4, Marshal.SizeOf<RuleBlobPortRange>(), "Marshal.SizeOf<port_range>()");
        Check.Equal(4, Unsafe.SizeOf<RuleBlobPortRange>(), "Unsafe.SizeOf<port_range>()");
        Check.Equal(4, typeof(RuleBlobPortRange).StructLayoutAttribute?.Pack ?? 0, "Pack(port_range)");
    }

    private static void PortRangeOffsets()
    {
        Check.Equal(0L, (long)Marshal.OffsetOf<RuleBlobPortRange>(nameof(RuleBlobPortRange.Start)), "port_range.start");
        Check.Equal(2L, (long)Marshal.OffsetOf<RuleBlobPortRange>(nameof(RuleBlobPortRange.End)), "port_range.end");
    }

    private static void NetworkSize()
    {
        Check.Equal(20, Marshal.SizeOf<RuleBlobNetworkPrefix>(), "Marshal.SizeOf<network_prefix>()");
        Check.Equal(20, Unsafe.SizeOf<RuleBlobNetworkPrefix>(), "Unsafe.SizeOf<network_prefix>()");
        Check.Equal(16, Unsafe.SizeOf<NetworkAddress>(), "sizeof(network[16])");
    }

    private static void NetworkOffsets()
    {
        (string Name, long Offset)[] expected =
        {
            (nameof(RuleBlobNetworkPrefix.PrefixLength), 0),
            (nameof(RuleBlobNetworkPrefix.Family), 1),
            (nameof(RuleBlobNetworkPrefix.Reserved0), 2),
            (nameof(RuleBlobNetworkPrefix.Reserved1), 3),
            (nameof(RuleBlobNetworkPrefix.Network), 4),
        };

        foreach ((string name, long offset) in expected)
        {
            Check.Equal(offset, (long)Marshal.OffsetOf<RuleBlobNetworkPrefix>(name), $"offsetof(network_prefix, {name})");
        }
    }

    private static void ManagedSizes()
    {
        Check.Equal(Marshal.SizeOf<RuleBlobHeader>(), Unsafe.SizeOf<RuleBlobHeader>(), "头部托管尺寸");
        Check.Equal(Marshal.SizeOf<RuleBlobRule>(), Unsafe.SizeOf<RuleBlobRule>(), "规则托管尺寸");
        Check.Equal(Marshal.SizeOf<RuleBlobProcessSelector>(), Unsafe.SizeOf<RuleBlobProcessSelector>(), "选择器托管尺寸");
        Check.Equal(Marshal.SizeOf<RuleBlobPortRange>(), Unsafe.SizeOf<RuleBlobPortRange>(), "端口记录托管尺寸");
        Check.Equal(Marshal.SizeOf<RuleBlobNetworkPrefix>(), Unsafe.SizeOf<RuleBlobNetworkPrefix>(), "网段记录托管尺寸");
    }

    private static void HeaderGoldenBytes()
    {
        RuleBlobHeader header = default;
        header.Magic = 0x31425257;
        header.Version = 2;
        header.HeaderSize = 56;
        header.TotalSize = 0x40;
        header.RuleCount = 2;
        header.FallbackAction = 1;
        header.RulesOffset = 56;
        header.ProcessPoolOffset = 72;
        header.ProcessPoolSize = 4;
        header.SelectorPoolOffset = 76;
        header.SelectorPoolSize = 8;
        header.NetworkPoolOffset = 84;
        header.NetworkPoolSize = 20;
        header.PortPoolOffset = 104;
        header.PortPoolSize = 4;

        byte[] actual = new byte[56];
        MemoryMarshal.Write(actual, in header);

        byte[] expected =
        {
            0x57, 0x52, 0x42, 0x31, // "WRB1"
            0x02, 0x00, // version = 2
            0x38, 0x00, // header_size = 56
            0x40, 0x00, 0x00, 0x00, // total_size
            0x02, 0x00, 0x00, 0x00, // rule_count
            0x01, 0x00, 0x00, 0x00, // fallback_action
            0x38, 0x00, 0x00, 0x00, // rules_offset
            0x48, 0x00, 0x00, 0x00, // process_pool_offset
            0x04, 0x00, 0x00, 0x00, // process_pool_size
            0x4C, 0x00, 0x00, 0x00, // selector_pool_offset
            0x08, 0x00, 0x00, 0x00, // selector_pool_size
            0x54, 0x00, 0x00, 0x00, // network_pool_offset
            0x14, 0x00, 0x00, 0x00, // network_pool_size
            0x68, 0x00, 0x00, 0x00, // port_pool_offset
            0x04, 0x00, 0x00, 0x00, // port_pool_size
        };

        Check.BytesEqual(expected, actual, "头部按主机小端写出的字节");
    }

    private static void MagicBytes()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(Array.Empty<PolicyRule>(), RuleAction.Block));
        Check.BytesEqual("WRB1"u8, blob.AsSpan(0, 4), "blob 头部前 4 字节");
    }

    private static void LittleEndianHost()
    {
        Check.True(BitConverter.IsLittleEndian, "blob 是主机字节序的 ABI，部署目标只有 Windows x64 小端");
    }

    private static void AbiVersionSeven()
    {
        Check.Equal(7U, ProbeIoctl.AbiVersion, "控制面 ABI 版本");
        Check.Equal(64, DeviceLayout.ProbeEventSize, "probe_event 尺寸");
        Check.Equal(264, DeviceLayout.ProbeStatsSize, "probe_stats 尺寸");
        Check.Equal(58L, (long)Marshal.OffsetOf<ProbeEvent>(nameof(ProbeEvent.Protocol)), "probe_event.protocol 偏移");
        Check.Equal(59L, (long)Marshal.OffsetOf<ProbeEvent>(nameof(ProbeEvent.Reserved0)), "probe_event.reserved0 偏移");
        Check.Equal(60L, (long)Marshal.OffsetOf<ProbeEvent>(nameof(ProbeEvent.MatchedRule)), "probe_event.matched_rule 偏移");
        Check.Equal(224L, (long)Marshal.OffsetOf<ProbeStats>(nameof(ProbeStats.UdpClassifyCalls)), "probe_stats.udp_classify_calls 偏移");
        Check.Equal(232L, (long)Marshal.OffsetOf<ProbeStats>(nameof(ProbeStats.UdpPermitted)), "probe_stats.udp_permitted 偏移");
        Check.Equal(240L, (long)Marshal.OffsetOf<ProbeStats>(nameof(ProbeStats.UdpBlocked)), "probe_stats.udp_blocked 偏移");
        Check.Equal(248L, (long)Marshal.OffsetOf<ProbeStats>(nameof(ProbeStats.UdpRedirected)), "probe_stats.udp_redirected 偏移");
        Check.Equal(256L, (long)Marshal.OffsetOf<ProbeStats>(nameof(ProbeStats.LastStatus)), "probe_stats.last_status 偏移");
        Check.Equal(260L, (long)Marshal.OffsetOf<ProbeStats>(nameof(ProbeStats.AbiVersion)), "probe_stats.abi_version 偏移");
        Check.Equal(32, DeviceLayout.ProbeForwardedInfoSize, "probe_forwarded_info 尺寸");
        Check.Equal(456, DeviceLayout.ProbeForwardedStatsSize, "probe_forwarded_stats 尺寸");
        Check.Equal(28L,
                    (long)Marshal.OffsetOf<ProbeForwardedInfo>(nameof(ProbeForwardedInfo.Reserved0)),
                    "probe_forwarded_info.reserved0 偏移");
        Check.Equal(88L,
                    (long)Marshal.OffsetOf<ProbeForwardedStats>(nameof(ProbeForwardedStats.SelfInjected)),
                    "probe_forwarded_stats.self_injected 偏移");
        Check.Equal(144L,
                    (long)Marshal.OffsetOf<ProbeForwardedStats>(nameof(ProbeForwardedStats.MatchedRedirect)),
                    "probe_forwarded_stats.matched_redirect 偏移");
        Check.Equal(392L,
                    (long)Marshal.OffsetOf<ProbeForwardedStats>(nameof(ProbeForwardedStats.LastStatus)),
                    "probe_forwarded_stats.last_status 偏移");
        Check.Equal(396L,
                    (long)Marshal.OffsetOf<ProbeForwardedStats>(nameof(ProbeForwardedStats.AbiVersion)),
                    "probe_forwarded_stats.abi_version 偏移");
        Check.Equal(280L,
                    (long)Marshal.OffsetOf<ProbeForwardedStats>(nameof(ProbeForwardedStats.UdpCreated)),
                    "probe_forwarded_stats.udp_created 偏移");
        Check.Equal(376L,
                    (long)Marshal.OffsetOf<ProbeForwardedStats>(nameof(ProbeForwardedStats.UdpMultiNb)),
                    "probe_forwarded_stats.udp_multi_nb 偏移");
        Check.Equal(28, DeviceLayout.ProbeForwardedConfigSize, "probe_forwarded_config 尺寸");
        Check.Equal(40, DeviceLayout.ProbeForwardedRedirectQuerySize, "probe_forwarded_redirect_query 尺寸");
        Check.Equal(88, DeviceLayout.ProbeForwardedRedirectSize, "probe_forwarded_redirect 尺寸");
        Check.Equal(28, DeviceLayout.ProbeForwardedIngressSize, "probe_forwarded_ingress 尺寸");
        Check.Equal(16, DeviceLayout.ProbeForwardedIngressConfigSize, "probe_forwarded_ingress_config 尺寸");

        Check.Equal(26L,
                    (long)Marshal.OffsetOf<ProbeForwardedRedirect>(nameof(ProbeForwardedRedirect.ServerAddress)),
                    "probe_forwarded_redirect.server_address 偏移");
    }
}
