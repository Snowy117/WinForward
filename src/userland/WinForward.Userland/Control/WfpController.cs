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
    private bool _stopped;

    // 没有 forwarded 段时下发的空表：清掉上一次异常退出可能留下的规则。空表 = 全部走 fallback permit。
    private static readonly byte[] EmptyForwardedBlob =
        ForwardedBlobCompiler.Compile(new ForwardedPolicyTable([], RuleAction.Permit));

    private WfpController(WfpDeviceClient device, RelayServer relay, TextWriter report)
    {
        _device = device;
        _relay = relay;
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
        try
        {
            ProbeStats stats = device.GetStats();
            if (stats.AbiVersion != ProbeIoctl.AbiVersion)
            {
                throw new InvalidOperationException(
                    $"驱动 ABI 版本 {stats.AbiVersion} 与产品侧 {ProbeIoctl.AbiVersion} 不一致，拒绝下发配置");
            }

            device.SetConfig(ProbeConfig.Disabled());

            ListenerSet listeners = ListenerSet.Bind();
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
            device.SetForwardedConfig(ProbeForwardedConfig.Disabled((ushort)relay.Port));
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
            ConfigureForwarded(device, settings, (ushort)relay.Port);

            uint processId = (uint)Environment.ProcessId;
            device.SetConfig(ProbeConfig.EnabledFor(processId, (ushort)relay.Port, new[] { processId }));
            VerifyConfig(device, relay.Port);
            ReportConfig(device, settings.Report);
            // 就绪信号必须在规则与配置都生效之后发：E2E 拿它当「可以建连了」的信号，
            // 提前打印会留下「进程说 ready、驱动还没启用」的窗口，门禁偶发失败就来自这里。
            if (settings.Socks5 is not null)
            {
                Report(settings.Report, $"controller-upstream socks5={settings.Socks5}");
            }

            Report(settings.Report, $"controller-ready pid={Environment.ProcessId} port={relay.Port} " +
                                    $"listener={DescribeListeners(listeners)}");

            return new WfpController(device, relay, settings.Report);
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
                device.SetForwardedConfig(ProbeForwardedConfig.Disabled((ushort)(relay?.Port ?? 0)));
                device.SetForwardedBlob(EmptyForwardedBlob);
                VerifyForwarded(device, settings.Report, EmptyForwardedBlob, 0, RuleAction.Permit);
                Report(settings.Report, "controller-forwarded-cleared rules=0 fallback=permit");
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Report(settings.Report,
                       $"controller-forwarded-clear-error {exception.GetType().Name}: {exception.Message}");
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
            _device.SetForwardedConfig(ProbeForwardedConfig.Disabled((ushort)_relay.Port));
            Report(_report, $"controller-forwarded-config-disabled enabled=0 port={_relay.Port}");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Report(_report, $"controller-disable-error {exception.GetType().Name}: {exception.Message}");
        }

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

        // 先停监听、等会话收尾，再报计数：否则 controller-stop 里的 bytes 可能还是 0。
        await _relay.DisposeAsync();
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
                       $"listener_pid={config.ListenerProcessId} self_count={config.SelfProcessCount}");

    }

    private static void VerifyConfig(WfpDeviceClient device, int port)
    {
        ProbeConfig config = device.GetConfig();
        if (config.Enabled != 1 || config.ListenerPort != (ushort)port ||
            config.ListenerProcessId != (uint)Environment.ProcessId || config.SelfProcessCount != 1)
        {
            throw new InvalidOperationException(
                $"驱动回读配置与下发的不同：enabled={config.Enabled} port={config.ListenerPort} " +
                $"pid={config.ListenerProcessId} self={config.SelfProcessCount}");
        }
    }

    /// <summary>
    /// 下发 forwarded 数据面配置：先 disabled（清表），再 enabled。所有校验都在启用之前完成；
    /// 找不到 listener 地址 L 时抛异常，由 Start 的失败路径回滚成 disabled。
    /// </summary>
    private static void ConfigureForwarded(WfpDeviceClient device, ControllerSettings settings, ushort listenerPort)
    {
        if (settings.Forwarded is null)
        {
            device.SetForwardedConfig(ProbeForwardedConfig.Disabled(listenerPort));
            VerifyForwardedConfig(device, enabled: false, listenerPort, null);
            Report(settings.Report,
                   $"controller-forwarded-config enabled=0 listener_port={listenerPort} reason=no-forwarded-policy");
            return;
        }

        IPAddress? listenerAddress = ResolveForwardedListenerAddress(settings);
        if (listenerAddress is null)
        {
            throw new InvalidOperationException(
                "forwarded 已配置，但找不到 listener 本地地址 L；请设置 ForwardedListenerAddress 或检查入口适配器的 IPv4 地址");
        }

        byte family = (byte)(listenerAddress.AddressFamily == AddressFamily.InterNetwork ? 4 : 6);
        device.SetForwardedConfig(ProbeForwardedConfig.Disabled(listenerPort, family));
        VerifyForwardedConfig(device, enabled: false, listenerPort, null);
        Report(settings.Report,
               $"controller-forwarded-config enabled=0 listener_port={listenerPort} (redirect table cleared)");

        device.SetForwardedConfig(ProbeForwardedConfig.EnabledFor(listenerPort, listenerAddress));
        VerifyForwardedConfig(device, enabled: true, listenerPort, listenerAddress);
        Report(settings.Report,
               $"controller-forwarded-config enabled=1 listener_port={listenerPort} " +
               $"listener={listenerAddress} family={family}");
    }

    private static IPAddress? ResolveForwardedListenerAddress(ControllerSettings settings)
    {
        if (settings.ForwardedListenerAddress is not null)
        {
            return settings.ForwardedListenerAddress;
        }

        if (settings.Forwarded is null)
        {
            return null;
        }

        uint? adapterId = null;
        foreach (ForwardedPolicyRule rule in settings.Forwarded.Rules)
        {
            if (rule.IngressAdapterId.HasValue)
            {
                adapterId = rule.IngressAdapterId;
                break;
            }
        }

        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            IPInterfaceProperties properties = adapter.GetIPProperties();
            int? index = properties.GetIPv4Properties()?.Index;
            if (adapterId.HasValue && (index is null || (uint)index.Value != adapterId.Value))
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

    private static void VerifyForwardedConfig(WfpDeviceClient device,
                                              bool enabled,
                                              ushort listenerPort,
                                              IPAddress? listenerAddress)
    {
        ProbeForwardedConfig config = device.GetForwardedConfig();
        if ((config.Enabled != 0) != enabled || config.ListenerPort != listenerPort)
        {
            throw new InvalidOperationException(
                $"驱动回读 forwarded 配置与下发的不同：enabled={config.Enabled} port={config.ListenerPort} " +
                $"期望 enabled={(enabled ? 1 : 0)} port={listenerPort}");
        }

        if (!enabled)
        {
            return;
        }

        if (listenerAddress is null || config.ListenerIP is null || !config.ListenerIP.Equals(listenerAddress))
        {
            throw new InvalidOperationException(
                $"驱动回读 forwarded listener 地址 {config.ListenerIP} 与下发的 {listenerAddress} 不一致");
        }
    }

    private static string DescribeListeners(ListenerSet listeners) =>
        string.Join(",", listeners.Listeners.Select(listener => listener.LocalEndpoint?.ToString() ?? "?"));

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
