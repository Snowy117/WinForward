using System.Runtime.InteropServices;
using WinForward.Userland.Device;

namespace WinForward.Userland.Tests;

internal static class DeviceAbiTests
{
    internal static void Register()
    {
        TestRunner.Add("device/布局断言在打开设备前就会跑", LayoutVerify);
        TestRunner.Add("device/配置结构字段按 C++ 头的顺序写进字节", ConfigRoundTrip);
        TestRunner.Add("device/启用配置拒绝非法参数", EnabledConfigValidation);
        TestRunner.Add("device/关闭配置只带版本号", DisabledConfig);
        TestRunner.Add("device/MakeIoctl 与常量一致", IoctlCodes);
        TestRunner.Add("device/事件结构可往返字节", EventRoundTrip);
        TestRunner.Add("device/统计结构可往返字节", StatsRoundTrip);
        TestRunner.Add("device/重定向上下文可往返字节", RedirectContextRoundTrip);
    }

    private static void LayoutVerify() => DeviceLayout.Verify();

    private static void ConfigRoundTrip()
    {
        ProbeConfig config = ProbeConfig.EnabledFor(4242, 51000, new[] { 7u, 4242u });
        byte[] bytes = new byte[DeviceLayout.ProbeConfigSize];
        MemoryMarshal.Write(bytes, in config);

        Check.Equal(2U, BitConverter.ToUInt32(bytes, 0), "abi_version 在偏移 0");
        Check.Equal(1U, BitConverter.ToUInt32(bytes, 4), "enabled 在偏移 4");
        Check.Equal(4242U, BitConverter.ToUInt32(bytes, 8), "listener_process_id 在偏移 8");
        Check.Equal((ushort)51000, BitConverter.ToUInt16(bytes, 12), "listener_port 在偏移 12");
        Check.Equal((ushort)0, BitConverter.ToUInt16(bytes, 14), "reserved0 是 0");
        Check.Equal(2U, BitConverter.ToUInt32(bytes, 16), "self_process_count 在偏移 16");
        Check.Equal(7U, BitConverter.ToUInt32(bytes, 20), "self_process_ids[0] 在偏移 20");
        Check.Equal(4242U, BitConverter.ToUInt32(bytes, 24), "self_process_ids[1] 紧随其后");
        Check.Equal(0U, BitConverter.ToUInt32(bytes, 28), "未使用的 self pid 槽位必须是 0");
    }

    private static void EnabledConfigValidation()
    {
        Check.Throws<ArgumentOutOfRangeException>(() => ProbeConfig.EnabledFor(0, 1234, Array.Empty<uint>()), "PID 为 0");
        Check.Throws<ArgumentOutOfRangeException>(() => ProbeConfig.EnabledFor(1, 0, Array.Empty<uint>()), "端口为 0");
        Check.Throws<ArgumentOutOfRangeException>(
            () => ProbeConfig.EnabledFor(1, 1234, new uint[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }), "self-PID 超过 8 个");
        Check.Throws<ArgumentOutOfRangeException>(() => ProbeConfig.EnabledFor(1, 1234, new uint[] { 0 }), "self-PID 为 0");
    }

    private static void DisabledConfig()
    {
        ProbeConfig config = ProbeConfig.Disabled();
        Check.Equal(ProbeIoctl.AbiVersion, config.AbiVersion, "abi_version");
        Check.Equal(0U, config.Enabled, "enabled = 0");
        Check.Equal(0U, config.SelfProcessCount, "没有 self-PID");
        Check.Equal(0U, config.ListenerPort, "没有监听端口");
    }

    private static void IoctlCodes()
    {
        Check.Equal(ProbeIoctl.SetConfigCode, ProbeIoctl.MakeIoctl(ProbeIoctl.SetConfigFunction), "SetConfig");
        Check.Equal(ProbeIoctl.GetStatsCode, ProbeIoctl.MakeIoctl(ProbeIoctl.GetStatsFunction), "GetStats");
        Check.Equal(ProbeIoctl.SetRuleBlobCode, ProbeIoctl.MakeIoctl(ProbeIoctl.SetRuleBlobFunction), "SetRuleBlob");
        Check.Equal(0x00222000u, ProbeIoctl.SetConfigCode, "CTL_CODE 的字节值");
        Check.Equal(0x0022201Cu, ProbeIoctl.MeasureProcessPathCode, "诊断接口的字节值");
    }

    private static void EventRoundTrip()
    {
        ProbeEvent source = default;
        source.Sequence = 7;
        source.ProcessId = 99;
        source.RemoteAddress[0] = 10;
        source.RemoteAddress[15] = 0xAB;
        source.LocalAddress[3] = 1;
        source.RemotePort = 443;
        source.LocalPort = 50000;
        source.RedirectPort = 1234;
        source.Decision = (byte)ProbeDecision.Redirected;
        source.Family = 4;
        source.Action = (byte)ProbeAction.Redirect;
        source.Branch = (byte)RedirectBranch.LocalAddress;
        source.MatchedRule = 3;

        byte[] bytes = new byte[DeviceLayout.ProbeEventSize];
        MemoryMarshal.Write(bytes, in source);
        ProbeEvent restored = MemoryMarshal.Read<ProbeEvent>(bytes);

        Check.Equal(7U, restored.Sequence, "sequence");
        Check.Equal(10, (int)restored.RemoteAddress[0], "remote_address[0]");
        Check.Equal(0xAB, (int)restored.RemoteAddress[15], "remote_address[15]");
        Check.Equal((ushort)443, restored.RemotePort, "remote_port");
        Check.Equal((byte)ProbeDecision.Redirected, restored.Decision, "decision");
        Check.Equal((byte)RedirectBranch.LocalAddress, restored.Branch, "branch");
        Check.Equal(3U, restored.MatchedRule, "matched_rule");
    }

    private static void StatsRoundTrip()
    {
        ProbeStats stats = default;
        stats.Redirected = ulong.MaxValue;
        stats.PermittedSelfProcess = 42;
        stats.LastStatus = 0xC000000D;
        stats.AbiVersion = ProbeIoctl.AbiVersion;

        byte[] bytes = new byte[DeviceLayout.ProbeStatsSize];
        MemoryMarshal.Write(bytes, in stats);
        ProbeStats restored = MemoryMarshal.Read<ProbeStats>(bytes);

        Check.Equal(ulong.MaxValue, restored.Redirected, "redirected");
        Check.Equal(42UL, restored.PermittedSelfProcess, "permitted_self_process");
        Check.Equal(0xC000000DU, restored.LastStatus, "last_status");
        Check.Equal(ProbeIoctl.AbiVersion, restored.AbiVersion, "abi_version");
    }

    private static void RedirectContextRoundTrip()
    {
        RedirectContext context = default;
        context.Magic = RedirectContext.MagicValue;
        context.Version = RedirectContext.VersionValue;
        context.RuleIndex = 5;
        context.ProcessId = 1234;
        context.OriginalPort = 443;
        context.Family = RedirectContext.FamilyIpv6;
        context.Protocol = RedirectContext.ProtocolTcp;
        context.OriginalAddress[0] = 0x20;
        context.OriginalAddress[15] = 0x01;

        byte[] bytes = new byte[DeviceLayout.RedirectContextSize];
        MemoryMarshal.Write(bytes, in context);
        RedirectContext restored = MemoryMarshal.Read<RedirectContext>(bytes);

        Check.Equal(RedirectContext.MagicValue, restored.Magic, "magic");
        Check.Equal(RedirectContext.VersionValue, restored.Version, "version");
        Check.Equal(5U, restored.RuleIndex, "rule_index");
        Check.Equal((ushort)443, restored.OriginalPort, "original_port");
        Check.Equal(RedirectContext.FamilyIpv6, restored.Family, "family");
        Check.Equal(0x20, (int)restored.OriginalAddress[0], "original_address[0]");
        Check.Equal(0x01, (int)restored.OriginalAddress[15], "original_address[15]");
    }
}
