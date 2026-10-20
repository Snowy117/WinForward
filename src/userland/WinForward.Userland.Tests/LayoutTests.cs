using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class LayoutTests
{
    internal static void Register()
    {
        TestRunner.Add("layout/头部 40 字节", HeaderSize);
        TestRunner.Add("layout/头部逐字段偏移", HeaderOffsets);
        TestRunner.Add("layout/规则 16 字节", RuleSize);
        TestRunner.Add("layout/规则逐字段偏移", RuleOffsets);
        TestRunner.Add("layout/网段记录 20 字节", NetworkSize);
        TestRunner.Add("layout/网段逐字段偏移", NetworkOffsets);
        TestRunner.Add("layout/托管尺寸与 Marshal 尺寸一致", ManagedSizes);
        TestRunner.Add("layout/头部黄金字节序列", HeaderGoldenBytes);
        TestRunner.Add("layout/blob 头部魔数在内存中是 WRB1", MagicBytes);
        TestRunner.Add("layout/部署目标是主机小端", LittleEndianHost);
    }

    private static void HeaderSize()
    {
        Check.Equal(40, Marshal.SizeOf<RuleBlobHeader>(), "Marshal.SizeOf<RuleBlobHeader>()");
        Check.Equal(40, Unsafe.SizeOf<RuleBlobHeader>(), "Unsafe.SizeOf<RuleBlobHeader>()");
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
            (nameof(RuleBlobHeader.NetworkPoolOffset), 32),
            (nameof(RuleBlobHeader.NetworkPoolSize), 36),
        };

        foreach ((string name, long offset) in expected)
        {
            Check.Equal(offset, (long)Marshal.OffsetOf<RuleBlobHeader>(name), $"offsetof(rule_blob_header, {name})");
        }
    }

    private static void RuleSize()
    {
        Check.Equal(16, Marshal.SizeOf<RuleBlobRule>(), "Marshal.SizeOf<RuleBlobRule>()");
        Check.Equal(16, Unsafe.SizeOf<RuleBlobRule>(), "Unsafe.SizeOf<RuleBlobRule>()");
        Check.Equal(4, typeof(RuleBlobRule).StructLayoutAttribute?.Pack ?? 0, "Pack(rule)");
    }

    private static void RuleOffsets()
    {
        (string Name, long Offset)[] expected =
        {
            (nameof(RuleBlobRule.ProcessIndex), 0),
            (nameof(RuleBlobRule.NetworkIndex), 4),
            (nameof(RuleBlobRule.RemotePortStart), 8),
            (nameof(RuleBlobRule.RemotePortEnd), 10),
            (nameof(RuleBlobRule.Protocol), 12),
            (nameof(RuleBlobRule.Family), 13),
            (nameof(RuleBlobRule.Action), 14),
            (nameof(RuleBlobRule.Reserved), 15),
        };

        foreach ((string name, long offset) in expected)
        {
            Check.Equal(offset, (long)Marshal.OffsetOf<RuleBlobRule>(name), $"offsetof(rule, {name})");
        }
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
        Check.Equal(Marshal.SizeOf<RuleBlobNetworkPrefix>(), Unsafe.SizeOf<RuleBlobNetworkPrefix>(), "网段记录托管尺寸");
    }

    private static void HeaderGoldenBytes()
    {
        RuleBlobHeader header = default;
        header.Magic = 0x31425257;
        header.Version = 1;
        header.HeaderSize = 40;
        header.TotalSize = 0x40;
        header.RuleCount = 2;
        header.FallbackAction = 1;
        header.RulesOffset = 40;
        header.ProcessPoolOffset = 72;
        header.ProcessPoolSize = 12;
        header.NetworkPoolOffset = 84;
        header.NetworkPoolSize = 20;

        byte[] actual = new byte[40];
        MemoryMarshal.Write(actual, in header);

        byte[] expected =
        {
            0x57, 0x52, 0x42, 0x31, // "WRB1"
            0x01, 0x00, // version = 1
            0x28, 0x00, // header_size = 40
            0x40, 0x00, 0x00, 0x00, // total_size
            0x02, 0x00, 0x00, 0x00, // rule_count
            0x01, 0x00, 0x00, 0x00, // fallback_action
            0x28, 0x00, 0x00, 0x00, // rules_offset
            0x48, 0x00, 0x00, 0x00, // process_pool_offset
            0x0C, 0x00, 0x00, 0x00, // process_pool_size
            0x54, 0x00, 0x00, 0x00, // network_pool_offset
            0x14, 0x00, 0x00, 0x00, // network_pool_size
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
}
