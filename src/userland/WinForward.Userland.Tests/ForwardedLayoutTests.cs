using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinForward.Userland.Forwarded;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class ForwardedLayoutTests
{
    internal static void Register()
    {
        TestRunner.Add("forwarded-layout/头部 40 字节", HeaderSize);
        TestRunner.Add("forwarded-layout/头部逐字段偏移", HeaderOffsets);
        TestRunner.Add("forwarded-layout/规则 24 字节", RuleSize);
        TestRunner.Add("forwarded-layout/规则逐字段偏移", RuleOffsets);
        TestRunner.Add("forwarded-layout/网段记录复用 host 的 20 字节布局", NetworkPrefixSize);
        TestRunner.Add("forwarded-layout/头部黄金字节序列", HeaderGoldenBytes);
        TestRunner.Add("forwarded-layout/规则黄金字节序列", RuleGoldenBytes);
        TestRunner.Add("forwarded-layout/blob 头部魔数在内存中是 WFB1", MagicBytes);
        TestRunner.Add("forwarded-layout/布局常量与 C++ 一致", LayoutConstants);
    }

    private static void HeaderSize()
    {
        Check.Equal(40, Marshal.SizeOf<ForwardedBlobHeader>(), "Marshal.SizeOf<ForwardedBlobHeader>()");
        Check.Equal(40, Unsafe.SizeOf<ForwardedBlobHeader>(), "Unsafe.SizeOf<ForwardedBlobHeader>()");
        Check.Equal(4, typeof(ForwardedBlobHeader).StructLayoutAttribute?.Pack ?? 0, "Pack(forwarded_blob_header)");
    }

    private static void HeaderOffsets()
    {
        (string Name, long Offset)[] expected =
        {
            (nameof(ForwardedBlobHeader.Magic), 0),
            (nameof(ForwardedBlobHeader.Version), 4),
            (nameof(ForwardedBlobHeader.HeaderSize), 6),
            (nameof(ForwardedBlobHeader.TotalSize), 8),
            (nameof(ForwardedBlobHeader.RuleCount), 12),
            (nameof(ForwardedBlobHeader.FallbackAction), 16),
            (nameof(ForwardedBlobHeader.RulesOffset), 20),
            (nameof(ForwardedBlobHeader.NetworkPoolOffset), 24),
            (nameof(ForwardedBlobHeader.NetworkPoolSize), 28),
            (nameof(ForwardedBlobHeader.Reserved), 32),
            (nameof(ForwardedBlobHeader.Reserved2), 36),
        };

        foreach ((string name, long offset) in expected)
        {
            Check.Equal(offset,
                        (long)Marshal.OffsetOf<ForwardedBlobHeader>(name),
                        $"offsetof(forwarded_blob_header, {name})");
        }
    }

    private static void RuleSize()
    {
        Check.Equal(24, Marshal.SizeOf<ForwardedBlobRule>(), "Marshal.SizeOf<ForwardedBlobRule>()");
        Check.Equal(24, Unsafe.SizeOf<ForwardedBlobRule>(), "Unsafe.SizeOf<ForwardedBlobRule>()");
        Check.Equal(4, typeof(ForwardedBlobRule).StructLayoutAttribute?.Pack ?? 0, "Pack(forwarded_blob_rule)");
    }

    private static void RuleOffsets()
    {
        (string Name, long Offset)[] expected =
        {
            (nameof(ForwardedBlobRule.IngressAdapterId), 0),
            (nameof(ForwardedBlobRule.ClientNetworkIndex), 4),
            (nameof(ForwardedBlobRule.RemoteNetworkIndex), 8),
            (nameof(ForwardedBlobRule.RemotePortStart), 12),
            (nameof(ForwardedBlobRule.RemotePortEnd), 14),
            (nameof(ForwardedBlobRule.UpstreamId), 16),
            (nameof(ForwardedBlobRule.Protocol), 20),
            (nameof(ForwardedBlobRule.Family), 21),
            (nameof(ForwardedBlobRule.Action), 22),
            (nameof(ForwardedBlobRule.Reserved), 23),
        };

        foreach ((string name, long offset) in expected)
        {
            Check.Equal(offset,
                        (long)Marshal.OffsetOf<ForwardedBlobRule>(name),
                        $"offsetof(forwarded_blob_rule, {name})");
        }
    }

    private static void NetworkPrefixSize()
    {
        Check.Equal(ForwardedBlobLayout.NetworkPrefixSize,
                    Marshal.SizeOf<RuleBlobNetworkPrefix>(),
                    "forwarded 网段记录与 host 的 RuleBlobNetworkPrefix 同尺寸");
        Check.Equal(ForwardedBlobLayout.NetworkAddressSize,
                    Unsafe.SizeOf<NetworkAddress>(),
                    "sizeof(network[16])");
    }

    private static void HeaderGoldenBytes()
    {
        ForwardedBlobHeader header = default;
        header.Magic = 0x31424657;
        header.Version = 1;
        header.HeaderSize = 40;
        header.TotalSize = 0x50;
        header.RuleCount = 2;
        header.FallbackAction = 1;
        header.RulesOffset = 40;
        header.NetworkPoolOffset = 0x58;
        header.NetworkPoolSize = 0x14;
        header.Reserved = 0;
        header.Reserved2 = 0;

        byte[] actual = new byte[40];
        MemoryMarshal.Write(actual, in header);

        byte[] expected =
        {
            0x57, 0x46, 0x42, 0x31, // "WFB1"
            0x01, 0x00, // version = 1
            0x28, 0x00, // header_size = 40
            0x50, 0x00, 0x00, 0x00, // total_size
            0x02, 0x00, 0x00, 0x00, // rule_count
            0x01, 0x00, 0x00, 0x00, // fallback_action
            0x28, 0x00, 0x00, 0x00, // rules_offset
            0x58, 0x00, 0x00, 0x00, // network_pool_offset
            0x14, 0x00, 0x00, 0x00, // network_pool_size
            0x00, 0x00, 0x00, 0x00, // reserved
            0x00, 0x00, 0x00, 0x00, // reserved2
        };

        Check.BytesEqual(expected, actual, "头部按主机小端写出的字节");
    }

    private static void RuleGoldenBytes()
    {
        ForwardedBlobRule rule = default;
        rule.IngressAdapterId = 0x01020304;
        rule.ClientNetworkIndex = 0xFFFFFFFF;
        rule.RemoteNetworkIndex = 0x14;
        rule.RemotePortStart = 8080;
        rule.RemotePortEnd = 443;
        rule.UpstreamId = 0x0A0B0C0D;
        rule.Protocol = 6;
        rule.Family = 4;
        rule.Action = 2;
        rule.Reserved = 0;

        byte[] actual = new byte[24];
        MemoryMarshal.Write(actual, in rule);

        byte[] expected =
        {
            0x04, 0x03, 0x02, 0x01, // ingress_adapter_id
            0xFF, 0xFF, 0xFF, 0xFF, // client_network_index = any
            0x14, 0x00, 0x00, 0x00, // remote_network_index
            0x90, 0x1F, // remote_port_start = 8080
            0xBB, 0x01, // remote_port_end = 443
            0x0D, 0x0C, 0x0B, 0x0A, // upstream_id
            0x06, // protocol = TCP
            0x04, // family = IPv4
            0x02, // action = redirect
            0x00, // reserved
        };

        Check.BytesEqual(expected, actual, "规则按主机小端写出的字节");
    }

    private static void MagicBytes()
    {
        byte[] blob = ForwardedBlobCompiler.Compile(
            new ForwardedPolicyTable(Array.Empty<ForwardedPolicyRule>(), RuleAction.Permit));
        Check.BytesEqual("WFB1"u8, blob.AsSpan(0, 4), "blob 头部前 4 字节");
    }

    private static void LayoutConstants()
    {
        Check.Equal(0x31424657U, ForwardedBlobLayout.Magic, "magic");
        Check.Equal((ushort)1, ForwardedBlobLayout.Version, "version");
        Check.Equal((ushort)40, ForwardedBlobLayout.HeaderSize, "header_size");
        Check.Equal(24, ForwardedBlobLayout.RuleSize, "rule_size");
        Check.Equal(20, ForwardedBlobLayout.NetworkPrefixSize, "network_prefix_size");
        Check.Equal(16, ForwardedBlobLayout.NetworkAddressSize, "network_address_size");
        Check.Equal(4, ForwardedBlobLayout.SegmentAlignment, "segment_alignment");
        Check.Equal(0xFFFFFFFFU, ForwardedBlobLayout.AdapterAny, "adapter_any");
        Check.Equal(0xFFFFFFFFU, ForwardedBlobLayout.UpstreamNone, "upstream_none");
        Check.Equal(0xFFFFFFFFU, ForwardedBlobLayout.PoolIndexAny, "pool_index_any");
    }
}
