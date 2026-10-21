using System.Globalization;
using WinForward.Userland.Device;

namespace WinForward.Controller;

/// <summary>把驱动里的配置、规则表信息与全部计数器打成 key value 行，供脚本断言与人工排查。</summary>
internal static class StateCommand
{
    internal static int Execute(string[] args)
    {
        CommandLine line = CommandLine.Parse(args);
        using ReportWriter report = new(line.Get("report"));

        try
        {
            using WfpDeviceClient device = WfpDeviceClient.Open();
            ProbeConfig config = device.GetConfig();
            ProbeRuleInfo info = device.GetRuleInfo();
            ProbeStats stats = device.GetStats();

            report.WriteLine($"config abi={config.AbiVersion} enabled={config.Enabled} " +
                             $"listener_port={config.ListenerPort} listener_pid={config.ListenerProcessId} " +
                             $"self_count={config.SelfProcessCount}");
            for (int index = 0; index < config.SelfProcessCount && index < ProbeIoctl.MaxSelfProcessIds; index++)
            {
                report.WriteLine($"self pid={config.SelfProcessIds[index]}");
            }

            report.WriteLine($"rule abi={info.AbiVersion} installed={info.Installed} generation={info.Generation} " +
                             $"count={info.RuleCount} fallback={info.FallbackAction} total_size={info.TotalSize} " +
                             $"process_pool={info.ProcessPoolSize} networks={info.NetworkPoolCount}");

            report.WriteLine($"stats abi={stats.AbiVersion} last_status=0x{stats.LastStatus:X8}");
            foreach ((string name, ulong value) in Counters(stats))
            {
                report.WriteLine($"stat {name} {value.ToString(CultureInfo.InvariantCulture)}");
            }

            return 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"读取驱动状态失败：{exception.Message}");
            return 1;
        }
    }

    private static IEnumerable<(string Name, ulong Value)> Counters(ProbeStats stats)
    {
        yield return ("classify_calls", stats.ClassifyCalls);
        yield return ("permitted", stats.Permitted);
        yield return ("blocked", stats.Blocked);
        yield return ("redirected", stats.Redirected);
        yield return ("rules_replaced", stats.RulesReplaced);
        yield return ("rejected_blobs", stats.RejectedBlobs);
        yield return ("contexts_allocated", stats.ContextsAllocated);
        yield return ("context_alloc_failures", stats.ContextAllocFailures);
        yield return ("apply_failures", stats.ApplyFailures);
        yield return ("redirect_failures", stats.RedirectFailures);
        yield return ("permitted_listener_port", stats.PermittedListenerPort);
        yield return ("permitted_condition_flags", stats.PermittedConditionFlags);
        yield return ("permitted_redirect_state", stats.PermittedRedirectState);
        yield return ("permitted_already_redirected", stats.PermittedAlreadyRedirected);
        yield return ("permitted_self_process", stats.PermittedSelfProcess);
        yield return ("permitted_rule", stats.MatchedPermit);
        yield return ("blocked_rule", stats.MatchedBlock);
        yield return ("redirected_rule", stats.MatchedRedirect);
        yield return ("fallback_permit", stats.FallbackPermit);
        yield return ("fallback_block", stats.FallbackBlock);
        yield return ("fallback_redirect", stats.FallbackRedirect);
        yield return ("deferred_blocks", stats.DeferredBlocks);
        yield return ("process_path_metadata_seen", stats.ProcessPathMetadataSeen);
        yield return ("udp_classify_calls", stats.UdpClassifyCalls);
        yield return ("udp_permitted", stats.UdpPermitted);
        yield return ("udp_blocked", stats.UdpBlocked);
        yield return ("udp_redirected", stats.UdpRedirected);
    }
}
