using WinForward.Userland.Device;
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
            relay = RelayServer.Start(listeners, settings.Report, settings.AttachRedirectRecords, settings.Socks5);

            device.SetRuleBlob(settings.RuleBlob);
            VerifyRules(device, settings);
            ReportRules(device, settings);
            if (settings.ForwardedBlob is not null)
            {
                Report(settings.Report,
                       ControllerReport.Forwarded(
                           settings.Forwarded?.Rules.Count ?? 0,
                           settings.ForwardedUpstreams.Count,
                           settings.ForwardedBlob.Length));
            }

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
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Report(_report, $"controller-disable-error {exception.GetType().Name}: {exception.Message}");
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
