using System.Net;
using System.Runtime.InteropServices;
using WinForward.Userland.Device;
using WinForward.Userland.Relay;

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
        TestRunner.Add("device/重定向目的地解析 TCP 与 UDP 上下文", RedirectDestinationProtocols);
        TestRunner.Add("device/中继拒绝非 TCP redirect context", RelayRefusalGate);
        TestRunner.Add("device/forwarded 结构可往返字节", ForwardedRoundTrip);
        TestRunner.Add("device/forwarded config 与 redirect 查询结构可往返字节", ForwardedRedirectRoundTrip);
    }

    private static void LayoutVerify() => DeviceLayout.Verify();

    private static void ConfigRoundTrip()
    {
        ProbeConfig config = ProbeConfig.EnabledFor(4242, 51000, new[] { 7u, 4242u });
        byte[] bytes = new byte[DeviceLayout.ProbeConfigSize];
        MemoryMarshal.Write(bytes, in config);

        Check.Equal(ProbeIoctl.AbiVersion, BitConverter.ToUInt32(bytes, 0), "abi_version 在偏移 0");
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
        Check.Equal(ProbeIoctl.SetForwardedBlobCode, ProbeIoctl.MakeIoctl(ProbeIoctl.SetForwardedBlobFunction), "SetForwardedBlob");
        Check.Equal(ProbeIoctl.GetForwardedInfoCode, ProbeIoctl.MakeIoctl(ProbeIoctl.GetForwardedInfoFunction), "GetForwardedInfo");
        Check.Equal(ProbeIoctl.GetForwardedStatsCode, ProbeIoctl.MakeIoctl(ProbeIoctl.GetForwardedStatsFunction), "GetForwardedStats");
        Check.Equal(ProbeIoctl.ResetForwardedStatsCode, ProbeIoctl.MakeIoctl(ProbeIoctl.ResetForwardedStatsFunction), "ResetForwardedStats");
        Check.Equal(0x00222020u, ProbeIoctl.SetForwardedBlobCode, "forwarded IOCTL 的字节值");
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
        source.Protocol = RedirectContext.ProtocolTcp;
        source.Reserved0 = 0;
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
        Check.Equal((byte)RedirectContext.ProtocolTcp, restored.Protocol, "protocol");
        Check.Equal((byte)0, restored.Reserved0, "reserved0");
        Check.Equal(3U, restored.MatchedRule, "matched_rule");
    }

    private static void StatsRoundTrip()
    {
        ProbeStats stats = default;
        stats.Redirected = ulong.MaxValue;
        stats.PermittedSelfProcess = 42;
        stats.UdpClassifyCalls = 11;
        stats.UdpPermitted = 12;
        stats.UdpBlocked = 13;
        stats.UdpRedirected = 14;
        stats.LastStatus = 0xC000000D;
        stats.AbiVersion = ProbeIoctl.AbiVersion;

        byte[] bytes = new byte[DeviceLayout.ProbeStatsSize];
        MemoryMarshal.Write(bytes, in stats);
        ProbeStats restored = MemoryMarshal.Read<ProbeStats>(bytes);

        Check.Equal(ulong.MaxValue, restored.Redirected, "redirected");
        Check.Equal(42UL, restored.PermittedSelfProcess, "permitted_self_process");
        Check.Equal(11UL, restored.UdpClassifyCalls, "udp_classify_calls");
        Check.Equal(12UL, restored.UdpPermitted, "udp_permitted");
        Check.Equal(13UL, restored.UdpBlocked, "udp_blocked");
        Check.Equal(14UL, restored.UdpRedirected, "udp_redirected");
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

    private static void RedirectDestinationProtocols()
    {
        RedirectContext context = default;
        context.Magic = RedirectContext.MagicValue;
        context.Version = RedirectContext.VersionValue;
        context.RuleIndex = 5;
        context.ProcessId = 1234;
        context.OriginalPort = 443;
        context.Family = RedirectContext.FamilyIpv4;
        context.OriginalAddress[0] = 203;
        context.OriginalAddress[3] = 7;

        byte[] bytes = new byte[DeviceLayout.RedirectContextSize];

        context.Protocol = RedirectContext.ProtocolTcp;
        MemoryMarshal.Write(bytes, in context);
        Check.True(RedirectDestination.TryParse(bytes, out RedirectDestination? tcp, out string tcpError),
                   $"TCP context 可解析：{tcpError}");
        Check.Equal((byte)RedirectContext.ProtocolTcp, tcp!.Protocol, "TCP protocol");
        Check.Equal("TCP", tcp.ProtocolText, "TCP 协议文本");
        Check.Equal(443, tcp.EndPoint.Port, "TCP 原始端口");

        context.Protocol = RedirectContext.ProtocolUdp;
        MemoryMarshal.Write(bytes, in context);
        Check.True(RedirectDestination.TryParse(bytes, out RedirectDestination? udp, out string udpError),
                   $"UDP context 可解析：{udpError}");
        Check.Equal((byte)RedirectContext.ProtocolUdp, udp!.Protocol, "UDP protocol");
        Check.Equal("UDP", udp.ProtocolText, "UDP 协议文本");

        context.Protocol = 18;
        MemoryMarshal.Write(bytes, in context);
        Check.True(!RedirectDestination.TryParse(bytes, out _, out string unknownError), "未知协议被拒绝");
        Check.True(unknownError.Contains("TCP/UDP", StringComparison.Ordinal), $"拒绝原因提到 TCP/UDP：{unknownError}");
    }

    private static void RelayRefusalGate()
    {
        RedirectContext context = default;
        context.Magic = RedirectContext.MagicValue;
        context.Version = RedirectContext.VersionValue;
        context.OriginalPort = 443;
        context.Family = RedirectContext.FamilyIpv4;
        context.OriginalAddress[0] = 127;
        context.OriginalAddress[3] = 1;

        byte[] bytes = new byte[DeviceLayout.RedirectContextSize];

        context.Protocol = RedirectContext.ProtocolTcp;
        MemoryMarshal.Write(bytes, in context);
        Check.True(RedirectDestination.TryParse(bytes, out RedirectDestination? tcp, out string tcpError),
                   $"TCP context 可解析：{tcpError}");
        Check.True(RelayServer.RelayRefusalReason(tcp!) is null, "TCP redirect context 不拒绝");

        context.Protocol = RedirectContext.ProtocolUdp;
        MemoryMarshal.Write(bytes, in context);
        Check.True(RedirectDestination.TryParse(bytes, out RedirectDestination? udp, out string udpError),
                   $"UDP context 可解析：{udpError}");
        Check.Equal("non-tcp-redirect-context", RelayServer.RelayRefusalReason(udp!),
                    "UDP redirect context 的拒绝原因");
        // 语义钉住：把 RelayRefusalReason 的 UDP 分支改成返回 null，上面这条断言会变红；
        // 它测的是拒绝决策本身，而不是 RedirectDestination 的解析结果。
    }

    private static void ForwardedRoundTrip()
    {
        ProbeForwardedInfo info = default;
        info.AbiVersion = ProbeIoctl.AbiVersion;
        info.Generation = 7;
        info.Installed = 1;
        info.RuleCount = 3;
        info.FallbackAction = 1;
        info.TotalSize = 640;
        info.NetworkPoolCount = 2;
        byte[] infoBytes = new byte[DeviceLayout.ProbeForwardedInfoSize];
        MemoryMarshal.Write(infoBytes, in info);

        Check.Equal(DeviceLayout.ProbeForwardedInfoSize, infoBytes.Length, "forwarded info 长度");
        Check.Equal(ProbeIoctl.AbiVersion, BitConverter.ToUInt32(infoBytes, 0), "forwarded abi_version 在偏移 0");
        Check.Equal(7U, BitConverter.ToUInt32(infoBytes, 4), "generation 在偏移 4");
        Check.Equal(1U, BitConverter.ToUInt32(infoBytes, 8), "installed 在偏移 8");
        Check.Equal(640U, BitConverter.ToUInt32(infoBytes, 20), "total_size 在偏移 20");
        Check.Equal(2U, BitConverter.ToUInt32(infoBytes, 24), "network_pool_count 在偏移 24");

        ProbeForwardedStats stats = default;
        stats.ClassifyCalls = 11;
        stats.InboundFrames = 5;
        stats.OutboundFrames = 6;
        stats.Permitted = 10;
        stats.Blocked = 1;
        stats.RejectedBlobs = 2;
        stats.TablesReplaced = 3;
        stats.MatchedRedirect = 1;
        stats.AbiVersion = ProbeIoctl.AbiVersion;
        byte[] statsBytes = new byte[DeviceLayout.ProbeForwardedStatsSize];
        MemoryMarshal.Write(statsBytes, in stats);

        Check.Equal(DeviceLayout.ProbeForwardedStatsSize, statsBytes.Length, "forwarded stats 长度");
        Check.Equal(11UL, BitConverter.ToUInt64(statsBytes, 0), "classify_calls 在偏移 0");
        Check.Equal(6UL, BitConverter.ToUInt64(statsBytes, 16), "outbound_frames 在偏移 16");
        Check.Equal(2UL, BitConverter.ToUInt64(statsBytes, 128), "rejected_blobs 在偏移 128");
        Check.Equal(ProbeIoctl.AbiVersion, BitConverter.ToUInt32(statsBytes, 292), "abi_version 在偏移 292");
        Check.Equal(1UL, BitConverter.ToUInt64(statsBytes, 144), "matched_redirect 在偏移 144");
    }

    private static void ForwardedRedirectRoundTrip()
    {
        ProbeForwardedConfig config = ProbeForwardedConfig.EnabledFor(40400, IPAddress.Parse("192.168.100.2"));
        byte[] configBytes = new byte[DeviceLayout.ProbeForwardedConfigSize];
        MemoryMarshal.Write(configBytes, in config);
        Check.Equal(1U, BitConverter.ToUInt32(configBytes, 0), "enabled 在偏移 0");
        Check.Equal((ushort)40400, BitConverter.ToUInt16(configBytes, 4), "listener_port 在偏移 4");
        Check.Equal((byte)4, configBytes[6], "family 在偏移 6");
        Check.Equal((byte)192, configBytes[8], "listener_address 在偏移 8");

        ProbeForwardedRedirectQuery query =
            ProbeForwardedRedirectQuery.ForClient(40400, new IPEndPoint(IPAddress.Parse("192.168.100.4"), 51234));
        byte[] queryBytes = new byte[DeviceLayout.ProbeForwardedRedirectQuerySize];
        MemoryMarshal.Write(queryBytes, in query);
        Check.Equal((ushort)40400, BitConverter.ToUInt16(queryBytes, 0), "query.listener_port 在偏移 0");
        Check.Equal((byte)4, queryBytes[2], "query.family 在偏移 2");
        Check.Equal((byte)192, queryBytes[4], "query.client_address 在偏移 4");

        ProbeForwardedRedirect redirect = default;
        redirect.Magic = ProbeForwardedRedirect.MagicValue;
        redirect.Version = ProbeForwardedRedirect.VersionValue;
        redirect.Family = 4;
        redirect.Protocol = RedirectContext.ProtocolTcp;
        redirect.ClientAddress[0] = 192;
        redirect.ClientPort = 51234;
        redirect.ServerAddress[0] = 10;
        redirect.ServerPort = 40250;
        redirect.ListenerPort = 40400;
        redirect.IngressIfIndex = 5;
        redirect.UpstreamId = 1;
        redirect.RuleIndex = 2;
        redirect.State = ProbeForwardedRedirect.StateEstablished;
        byte[] redirectBytes = new byte[DeviceLayout.ProbeForwardedRedirectSize];
        MemoryMarshal.Write(redirectBytes, in redirect);
        ProbeForwardedRedirect restored = MemoryMarshal.Read<ProbeForwardedRedirect>(redirectBytes);
        Check.Equal(ProbeForwardedRedirect.MagicValue, restored.Magic, "redirect.magic");
        Check.Equal(ProbeForwardedRedirect.VersionValue, restored.Version, "redirect.version");
        Check.Equal((ushort)40250, restored.ServerPort, "redirect.server_port");
        Check.Equal(5U, restored.IngressIfIndex, "redirect.ingress_ifindex");
        Check.Equal(1U, restored.UpstreamId, "redirect.upstream_id");
        Check.Equal(2U, restored.RuleIndex, "redirect.rule_index");
        Check.Equal(ProbeForwardedRedirect.StateEstablished, restored.State, "redirect.state");
        Check.Equal("10.0.0.0", restored.ServerIP.ToString(), "redirect.server_ip 只取 IPv4 前 4 字节");
    }
}
