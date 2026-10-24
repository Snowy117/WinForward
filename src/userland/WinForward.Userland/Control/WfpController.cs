using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WinForward.Userland.Device;
using WinForward.Userland.Forwarded;
using WinForward.Userland.Policy;
using WinForward.Userland.Relay;

namespace WinForward.Userland.Control;

/// <summary>
/// 产品进程的生命周期：下发规则 → 起监听 → 把监听端口与 self-PID 告诉驱动 → 中继 → 干净退出。
/// </summary>
/// <remarks>
/// 退出顺序是「先停重定向、再停监听」：先 <c>enabled = 0</c>，新的连接不再被改道，
/// 然后关监听与中继。驱动不需要卸载，下一次启动只要重新下发一遍配置。
///
/// 启动顺序是「先清旧配置、再起监听、再换规则、最后启用」：上一次进程异常退出时驱动里可能还留着
/// 指向死端口的配置，先把 <c>enabled</c> 置 0 再动别的，任何时刻都不会把连接改道到一个没在听的端口。
/// </remarks>
public sealed class WfpController : IAsyncDisposable
{
    private readonly WfpDeviceClient _device;
    private readonly RelayServer _relay;
    private readonly TextWriter _report;
    private readonly object _reportGate = new();
    private readonly ForwardedUdpRelay? _udpRelay;
    private readonly IReadOnlyList<ProbeForwardedIngress> _forwardedIngresses;
    private readonly bool _forwardedIngressMode;
    private readonly BlockSink _blockSink;
    private bool _stopped;


    // 没有 forwarded 段时下发的空表：清掉上一次异常退出可能留下的规则。空表 = 全部走 fallback permit。
    private static readonly byte[] EmptyForwardedBlob =
        ForwardedBlobCompiler.Compile(new ForwardedPolicyTable([], RuleAction.Permit));

    private WfpController(WfpDeviceClient device,
                          RelayServer relay,
                          ForwardedUdpRelay? udpRelay,
                          IReadOnlyList<ProbeForwardedIngress> forwardedIngresses,
                          bool forwardedIngressMode,
                          BlockSink blockSink,
                          TextWriter report)
    {
        _device = device;
        _relay = relay;
        _udpRelay = udpRelay;
        _forwardedIngresses = forwardedIngresses;
        _forwardedIngressMode = forwardedIngressMode;
        _blockSink = blockSink;
        _report = report;

    }

    public int ListenerPort => _relay.Port;

    public static WfpController Start(ControllerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.Policy);
        ArgumentNullException.ThrowIfNull(settings.RuleBlob);

        WfpDeviceClient device = WfpDeviceClient.Open();
        RelayServer? relay = null;
        ForwardedUdpRelay? udpRelay = null;
        BlockSink? blockSink = null;
        IReadOnlyList<ProbeForwardedIngress> ingresses = Array.Empty<ProbeForwardedIngress>();
        bool ingressMode = settings.Forwarded is not null;

        try
        {
            ProbeStats stats = device.GetStats();
            if (stats.AbiVersion != ProbeIoctl.AbiVersion)
            {
                throw new InvalidOperationException(
                    $"驱动 ABI 版本 {stats.AbiVersion} 与产品侧 {ProbeIoctl.AbiVersion} 不一致，拒绝下发配置");
            }

            device.SetConfig(ProbeConfig.Disabled());

            ForwardedListenerPlan plan = ResolveForwardedListenerPlan(settings);
            ListenerSet listeners = ListenerSet.Bind(plan.Addresses);
            ingresses = plan.Entries
                            .Select(entry => ProbeForwardedIngress.For(entry.Address, (ushort)listeners.Port, entry.IfIndex))
                            .ToArray();

            // 先保留 sink 端口再下发 blob：驱动要求 enabled 期间 sink 端口稳定，
            // 且含 block 的表必须有非 0 端口才能启用。
            blockSink = BlockSink.Bind(listeners.Port);

            relay = RelayServer.Start(listeners,
                                       settings.Report,
                                       settings.AttachRedirectRecords,
                                       settings.Socks5,
                                       new IRedirectContextSource[]
                                       {
                                           new ForwardedRedirectContextSource(device),
                                           new HostRedirectContextSource(),
                                       },
                                       settings.ForwardedUpstreams);

            // 先关掉 forwarded 数据面（清 redirect 表），再换规则 blob：上一次进程异常退出留下的
            // enabled 配置不会在新 listener 就绪前把连接改道。
            if (ingressMode)
            {
                device.SetForwardedIngressConfig(
                    new ProbeForwardedIngressConfig { Enabled = false, Ingresses = ingresses });
            }
            else
            {
                device.SetForwardedConfig(ProbeForwardedConfig.Disabled((ushort)relay.Port));
            }

            device.SetRuleBlob(settings.RuleBlob);
            VerifyRules(device, settings);
            ReportRules(device, settings);
            if (settings.ForwardedBlob is not null)
            {
                device.SetForwardedBlob(settings.ForwardedBlob);
                VerifyForwarded(device, settings.Report, settings.ForwardedBlob,
                                settings.Forwarded?.Rules.Count ?? 0,
                                settings.Forwarded?.FallbackAction ?? RuleAction.Permit);
                Report(settings.Report,
                       ControllerReport.Forwarded(
                           settings.Forwarded?.Rules.Count ?? 0,
                           settings.ForwardedUpstreams.Count,
                           settings.ForwardedBlob.Length));
            }
            else
            {
                device.SetForwardedBlob(EmptyForwardedBlob);
                VerifyForwarded(device, settings.Report, EmptyForwardedBlob, 0, RuleAction.Permit);
                Report(settings.Report, "controller-forwarded-cleared rules=0 fallback=permit");
            }
            ConfigureForwarded(device, settings, (ushort)relay.Port, ingresses);

            if (settings.Forwarded is not null)
            {
                udpRelay = ForwardedUdpRelay.Start(listeners.UdpSockets, device, settings.ForwardedUpstreams,
                                                   relay.Port, settings.Report);
            }

            uint processId = (uint)Environment.ProcessId;
            device.SetConfig(ProbeConfig.EnabledFor(processId, (ushort)relay.Port, new[] { processId }, (uint)blockSink.Port));
            VerifyConfig(device, relay.Port, blockSink.Port);
            ReportConfig(device, settings.Report);
            // 就绪信号必须在规则与配置都生效之后发：E2E 拿它当「可以建连了」的信号，
            // 提前打印会留下「进程说 ready、驱动还没启用」的窗口，门禁偶发失败就来自这里。
            if (settings.Socks5 is not null)
            {
                Report(settings.Report, $"controller-upstream socks5={settings.Socks5}");
            }

            Report(settings.Report, $"controller-ready pid={Environment.ProcessId} port={relay.Port} " +
                                    $"listener={DescribeListeners(listeners)}");

            return new WfpController(device, relay, udpRelay, ingresses, ingressMode, blockSink, settings.Report);
        }
        catch
        {
            // 失败路径不能把驱动留在「启用 + 指向死端口」的状态。
            try
            {
                device.SetConfig(ProbeConfig.Disabled());
            }
            catch (Exception exception)
            {
                Report(settings.Report, $"controller-rollback-error {exception.GetType().Name}: {exception.Message}");
            }

            try
            {
                if (ingressMode)
                {
                    device.SetForwardedIngressConfig(
                        new ProbeForwardedIngressConfig { Enabled = false, Ingresses = ingresses });
                }
                else
                {
                    device.SetForwardedConfig(ProbeForwardedConfig.Disabled((ushort)(relay?.Port ?? 0)));
                }

                device.SetForwardedBlob(EmptyForwardedBlob);
                VerifyForwarded(device, settings.Report, EmptyForwardedBlob, 0, RuleAction.Permit);
                Report(settings.Report, "controller-forwarded-cleared rules=0 fallback=permit");
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Report(settings.Report,
                       $"controller-forwarded-clear-error {exception.GetType().Name}: {exception.Message}");
            }

            blockSink?.Dispose();
            if (udpRelay is not null)
            {
                udpRelay.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            if (relay is not null)
            {
                relay.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            device.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;

        try
        {
            _device.SetConfig(ProbeConfig.Disabled());
            Report(_report, "controller-config-disabled enabled=0");
            if (_forwardedIngressMode)
            {
                _device.SetForwardedIngressConfig(
                    new ProbeForwardedIngressConfig { Enabled = false, Ingresses = _forwardedIngresses });
                Report(_report, $"controller-forwarded-ingress-config-disabled enabled=0 " +
                                $"count={_forwardedIngresses.Count} port={_relay.Port}");
            }
            else
            {
                _device.SetForwardedConfig(ProbeForwardedConfig.Disabled((ushort)_relay.Port));
                Report(_report, $"controller-forwarded-config-disabled enabled=0 port={_relay.Port}");
            }

        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Report(_report, $"controller-disable-error {exception.GetType().Name}: {exception.Message}");
        }

        // 驱动 disabled 之后 sink 端口才允许释放；反过来的话，驱动仍会把 block 命中改道到
        // 一个已经没人接的端口。
        _blockSink.Dispose();

        // 先停监听、等会话收尾，再报计数：否则 controller-stop 里的 bytes 可能还是 0。
        if (_udpRelay is not null)
        {
            await _udpRelay.DisposeAsync();
        }
        await _relay.DisposeAsync();

        try
        {
            _device.SetForwardedBlob(EmptyForwardedBlob);
            VerifyForwarded(_device, _report, EmptyForwardedBlob, 0, RuleAction.Permit);
            Report("controller-forwarded-cleared rules=0 fallback=permit");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Report($"controller-forwarded-clear-error {exception.GetType().Name}: {exception.Message}");
        }
        Report(_report, $"controller-stop accepted={_relay.Accepted} refused={_relay.Refused} " +
                        $"bytes_in={_relay.BytesClientToServer} bytes_out={_relay.BytesServerToClient}");

        _device.Dispose();
    }

    private static void VerifyRules(WfpDeviceClient device, ControllerSettings settings)
    {
        ProbeRuleInfo info = device.GetRuleInfo();
        if (info.Installed != 1)
        {
            throw new InvalidOperationException("规则 blob 下发后驱动报告 installed=0");
        }

        if (info.RuleCount != settings.Policy.Rules.Count)
        {
            throw new InvalidOperationException(
                $"驱动回读规则数 {info.RuleCount} 与配置的 {settings.Policy.Rules.Count} 不一致");
        }

        if (info.FallbackAction != (uint)settings.Policy.FallbackAction)
        {
            throw new InvalidOperationException(
                $"驱动回读兜底动作 {info.FallbackAction} 与配置的 {(uint)settings.Policy.FallbackAction} 不一致");
        }

        if (info.TotalSize != settings.RuleBlob.Length)
        {
            throw new InvalidOperationException(
                $"驱动回读 blob 长度 {info.TotalSize} 与下发的 {settings.RuleBlob.Length} 不一致");
        }
    }

    private static void VerifyForwarded(WfpDeviceClient device,
                                        TextWriter report,
                                        byte[] blob,
                                        int expectedRules,
                                        RuleAction expectedFallback)
    {
        ProbeForwardedInfo info = device.GetForwardedInfo();
        if (info.AbiVersion != ProbeIoctl.AbiVersion)
        {
            throw new InvalidOperationException(
                $"forwarded 表 ABI 版本 {info.AbiVersion} 与产品侧 {ProbeIoctl.AbiVersion} 不一致");
        }

        if (info.Installed != 1)
        {
            throw new InvalidOperationException("forwarded blob 下发后驱动报告 installed=0");
        }

        if (info.RuleCount != (uint)expectedRules)
        {
            throw new InvalidOperationException(
                $"驱动回读 forwarded 规则数 {info.RuleCount} 与下发的 {expectedRules} 不一致");
        }

        if (info.FallbackAction != (uint)expectedFallback)
        {
            throw new InvalidOperationException(
                $"驱动回读 forwarded 兜底动作 {info.FallbackAction} 与下发的 {(uint)expectedFallback} 不一致");
        }

        if (info.TotalSize != (uint)blob.Length)
        {
            throw new InvalidOperationException(
                $"驱动回读 forwarded blob 长度 {info.TotalSize} 与下发的 {blob.Length} 不一致");
        }

        Report(report,
               ControllerReport.ForwardedInstalled(
                   info.Generation, info.RuleCount, info.FallbackAction, (int)info.TotalSize));
    }


    private static void ReportRules(WfpDeviceClient device, ControllerSettings settings)
    {
        ProbeRuleInfo info = device.GetRuleInfo();
        Report(settings.Report,
               $"controller-rules installed={info.Installed} generation={info.Generation} count={info.RuleCount} " +
               $"fallback={info.FallbackAction} blob={info.TotalSize} process_pool={info.ProcessPoolSize} " +
               $"networks={info.NetworkPoolCount}");
    }

    private static void ReportConfig(WfpDeviceClient device, TextWriter report)
    {
        ProbeConfig config = device.GetConfig();
        Report(report, $"controller-config enabled={config.Enabled} listener_port={config.ListenerPort} " +
                       $"listener_pid={config.ListenerProcessId} self_count={config.SelfProcessCount} block-sink-port={config.BlockSinkPort}");

    }

    private static void VerifyConfig(WfpDeviceClient device, int port, int blockSinkPort)
    {
        ProbeConfig config = device.GetConfig();
        if (config.Enabled != 1 || config.ListenerPort != (ushort)port ||
            config.ListenerProcessId != (uint)Environment.ProcessId || config.SelfProcessCount != 1 ||
            config.BlockSinkPort != (uint)blockSinkPort)
        {
            throw new InvalidOperationException(
                $"驱动回读配置与下发的不同：enabled={config.Enabled} port={config.ListenerPort} " +
                $"pid={config.ListenerProcessId} self={config.SelfProcessCount} block_sink_port={config.BlockSinkPort} expected={blockSinkPort}");
        }
    }

    private sealed record ForwardedListenerEntry(uint IfIndex, IPAddress Address);

    private sealed record ForwardedListenerPlan(IReadOnlyList<IPAddress> Addresses,
                                                IReadOnlyList<ForwardedListenerEntry> Entries);

    /// <summary>
    /// 下发 forwarded 数据面配置：先 disabled（清表），再 enabled。所有校验都在启用之前完成；
    /// 找不到 listener 地址 L 时抛异常，由 Start 的失败路径回滚成 disabled。
    /// </summary>
    private static void ConfigureForwarded(WfpDeviceClient device,
                                           ControllerSettings settings,
                                           ushort listenerPort,
                                           IReadOnlyList<ProbeForwardedIngress> ingresses)
    {
        if (settings.Forwarded is null)
        {
            device.SetForwardedConfig(ProbeForwardedConfig.Disabled(listenerPort));
            VerifyForwardedConfig(device, enabled: false, listenerPort, null);
            Report(settings.Report,
                   $"controller-forwarded-config enabled=0 listener_port={listenerPort} reason=no-forwarded-policy");
            return;
        }

        if (ingresses.Count == 0)
        {
            throw new InvalidOperationException("forwarded 已配置，但入口 listener 映射为空");
        }

        device.SetForwardedIngressConfig(new ProbeForwardedIngressConfig { Enabled = false, Ingresses = ingresses });
        VerifyForwardedIngressConfig(device, enabled: false, ingresses);
        Report(settings.Report,
               $"controller-forwarded-ingress-config enabled=0 count={ingresses.Count} " +
               $"listener_port={listenerPort} (redirect table cleared)");

        device.SetForwardedIngressConfig(ProbeForwardedIngressConfig.EnabledFor(ingresses));
        VerifyForwardedIngressConfig(device, enabled: true, ingresses);
        foreach (ProbeForwardedIngress ingress in ingresses)
        {
            string index = ingress.IfIndex == ProbeIoctl.ForwardedIngressAnyIfIndex
                               ? "any"
                               : ingress.IfIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Report(settings.Report,
                   $"controller-forwarded-ingress ifindex={index} listener={ingress.ListenerIP}:{ingress.ListenerPort} " +
                   $"family={ingress.Family}");
        }
    }

    private static ForwardedListenerPlan ResolveForwardedListenerPlan(ControllerSettings settings)
    {
        if (settings.Forwarded is null)
        {
            return new ForwardedListenerPlan(Array.Empty<IPAddress>(), Array.Empty<ForwardedListenerEntry>());
        }

        var entries = new List<ForwardedListenerEntry>();
        var addresses = new List<IPAddress>();
        bool wildcard = false;
        foreach (ForwardedPolicyRule rule in settings.Forwarded.Rules)
        {
            if (rule.IngressAdapterId is not uint ifIndex)
            {
                wildcard = true;
                continue;
            }

            // 产品策略：每个 ifindex 只解析一个 L。同一 ifindex 多 L 是驱动/夹具能力，
            // 产品配置没有“按规则选 L”的字段，不把不确定的映射暴露给用户。
            if (entries.Any(entry => entry.IfIndex == ifIndex))

            {
                continue;
            }

            IPAddress address = settings.ForwardedListenerAddress ?? FindAdapterAddress(ifIndex)
                ?? throw new InvalidOperationException(
                    $"入口 ifIndex={ifIndex} 没有可用的 IPv4 地址做 forwarded listener L；" +
                    "请设置 ForwardedListenerAddress 或检查入口适配器");
            entries.Add(new ForwardedListenerEntry(ifIndex, address));
            AddAddress(addresses, address);
        }

        if (wildcard || entries.Count == 0)
        {
            IPAddress address = settings.ForwardedListenerAddress
                ?? (entries.Count > 0 ? entries[0].Address : FindFirstAdapterAddress())
                ?? throw new InvalidOperationException(
                    "forwarded 已配置，但找不到 listener 本地地址 L；请设置 ForwardedListenerAddress 或检查入口适配器的 IPv4 地址");
            entries.Add(new ForwardedListenerEntry(ProbeIoctl.ForwardedIngressAnyIfIndex, address));
            AddAddress(addresses, address);
        }

        return new ForwardedListenerPlan(addresses, entries);
    }

    private static void AddAddress(List<IPAddress> addresses, IPAddress address)
    {
        if (!addresses.Contains(address))
        {
            addresses.Add(address);
        }
    }

    private static IPAddress? FindAdapterAddress(uint ifIndex)
    {
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            IPInterfaceProperties properties = adapter.GetIPProperties();
            int? index = properties.GetIPv4Properties()?.Index;
            if (index is null || (uint)index.Value != ifIndex)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(unicast.Address))
                {
                    return unicast.Address;
                }
            }
        }

        return null;
    }

    private static IPAddress? FindFirstAdapterAddress()
    {
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(unicast.Address))
                {
                    return unicast.Address;
                }
            }
        }

        return null;
    }

    private static void VerifyForwardedIngressConfig(WfpDeviceClient device,
                                                     bool enabled,
                                                     IReadOnlyList<ProbeForwardedIngress> expected)
    {
        ProbeForwardedIngressConfig config = device.GetForwardedIngressConfig();
        if (config.AbiVersion != ProbeIoctl.AbiVersion)
        {
            throw new InvalidOperationException(
                $"forwarded 入口配置 ABI 版本 {config.AbiVersion} 与产品侧 {ProbeIoctl.AbiVersion} 不一致");
        }

        if (config.Enabled != enabled || config.Ingresses.Count != expected.Count)
        {
            throw new InvalidOperationException(
                $"驱动回读 forwarded 入口配置与下发的不同：enabled={config.Enabled} count={config.Ingresses.Count} " +
                $"期望 enabled={enabled} count={expected.Count}");
        }

        for (int index = 0; index < expected.Count; index++)
        {
            ProbeForwardedIngress actual = config.Ingresses[index];
            ProbeForwardedIngress wanted = expected[index];
            if (actual.IfIndex != wanted.IfIndex || actual.ListenerPort != wanted.ListenerPort ||
                actual.Family != wanted.Family || actual.ListenerIP is null ||
                !actual.ListenerIP.Equals(wanted.ListenerIP) || actual.Reserved1 != 0)
            {
                throw new InvalidOperationException(
                    $"驱动回读 forwarded 入口记录不一致：index={index} " +
                    $"actual=(ifindex={actual.IfIndex}, {actual.ListenerIP}:{actual.ListenerPort}) " +
                    $"wanted=(ifindex={wanted.IfIndex}, {wanted.ListenerIP}:{wanted.ListenerPort})");
            }
        }
    }

    private static void VerifyForwardedConfig(WfpDeviceClient device,
                                              bool enabled,
                                              ushort listenerPort,
                                              IPAddress? listenerAddress)
    {
        ProbeForwardedConfig config = device.GetForwardedConfig();
        if ((config.Enabled != 0) != enabled)
        {
            throw new InvalidOperationException(
                $"驱动回读 forwarded 配置与下发的不同：enabled={config.Enabled} 期望 enabled={(enabled ? 1 : 0)}");
        }

        if (!enabled)
        {
            return;
        }
        if (config.ListenerPort != listenerPort)
        {
            throw new InvalidOperationException(
                $"驱动回读 forwarded listener 端口 {config.ListenerPort} 与下发的 {listenerPort} 不一致");
        }

        if (listenerAddress is null || config.ListenerIP is null || !config.ListenerIP.Equals(listenerAddress))
        {
            throw new InvalidOperationException(
                $"驱动回读 forwarded listener 地址 {config.ListenerIP} 与下发的 {listenerAddress} 不一致");
        }
    }


    private static string DescribeListeners(ListenerSet listeners) =>
        string.Join(",", listeners.Endpoints.Select(endpoint => endpoint.EndPoint.ToString()));

    private void Report(string line)
    {
        lock (_reportGate)
        {
            _report.WriteLine(line);
            _report.Flush();
        }
    }

    private static void Report(TextWriter writer, string line)
    {
        lock (writer)
        {
            writer.WriteLine(line);
            writer.Flush();
        }
    }

}
