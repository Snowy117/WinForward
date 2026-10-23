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

            ProbeForwardedInfo forwardedInfo = device.GetForwardedInfo();
            ProbeForwardedStats forwardedStats = device.GetForwardedStats();
            ProbeForwardedIngressConfig forwardedIngress = device.GetForwardedIngressConfig();
            report.WriteLine($"forwarded abi={forwardedInfo.AbiVersion} installed={forwardedInfo.Installed} " +
                             $"generation={forwardedInfo.Generation} rules={forwardedInfo.RuleCount} " +
                             $"fallback={forwardedInfo.FallbackAction} total_size={forwardedInfo.TotalSize} " +
                             $"networks={forwardedInfo.NetworkPoolCount} enabled={(forwardedIngress.Enabled ? 1 : 0)} " +
                             $"ingresses={forwardedIngress.Ingresses.Count}");
            foreach (ProbeForwardedIngress ingress in forwardedIngress.Ingresses)
            {
                string index = ingress.IfIndex == ProbeIoctl.ForwardedIngressAnyIfIndex
                                   ? "any"
                                   : ingress.IfIndex.ToString(CultureInfo.InvariantCulture);
                report.WriteLine($"forwarded-ingress ifindex={index} family={ingress.Family} " +
                                 $"listener={ingress.ListenerIP}:{ingress.ListenerPort}");
            }

            report.WriteLine($"forwarded-stats abi={forwardedStats.AbiVersion} " +
                             $"last_status=0x{forwardedStats.LastStatus:X8} " +
                             $"redirect_active={forwardedStats.RedirectActive} " +
                             $"redirect_high_water={forwardedStats.RedirectHighWater}");
            foreach ((string name, ulong value) in ForwardedCounters(forwardedStats))
            {
                report.WriteLine($"forwarded-stat {name} {value.ToString(CultureInfo.InvariantCulture)}");
            }

            return 0;

        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"读取驱动状态失败：{exception.Message}");
            return 1;
        }
    }

    private static IEnumerable<(string Name, ulong Value)> ForwardedCounters(ProbeForwardedStats stats)
    {
        yield return ("classify_calls", stats.ClassifyCalls);
        yield return ("inbound_frames", stats.InboundFrames);
        yield return ("outbound_frames", stats.OutboundFrames);
        yield return ("permitted", stats.Permitted);
        yield return ("blocked", stats.Blocked);
        yield return ("matched", stats.Matched);
        yield return ("matched_permit", stats.MatchedPermit);
        yield return ("matched_block", stats.MatchedBlock);
        yield return ("matched_redirect", stats.MatchedRedirect);
        yield return ("fallback", stats.Fallback);
        yield return ("fallback_permit", stats.FallbackPermit);
        yield return ("fallback_block", stats.FallbackBlock);
        yield return ("self_injected", stats.SelfInjected);
        yield return ("no_table", stats.NoTable);
        yield return ("malformed", stats.Malformed);
        yield return ("non_ip", stats.NonIp);
        yield return ("no_write_right", stats.NoWriteRight);
        yield return ("rejected_blobs", stats.RejectedBlobs);
        yield return ("tables_replaced", stats.TablesReplaced);
        yield return ("redirected", stats.Redirected);
        yield return ("redirect_created", stats.RedirectCreated);
        yield return ("redirect_ingress", stats.RedirectIngress);
        yield return ("redirect_return", stats.RedirectReturn);
        yield return ("redirect_blocked", stats.RedirectBlocked);
        yield return ("redirect_return_failed", stats.RedirectReturnFailed);
        yield return ("redirect_return_miss", stats.RedirectReturnMiss);
        yield return ("redirect_return_ambiguous", stats.RedirectReturnAmbiguous);
        yield return ("redirect_no_config", stats.RedirectNoConfig);
        yield return ("redirect_no_state", stats.RedirectNoState);
        yield return ("redirect_no_ingress", stats.RedirectNoIngress);
        yield return ("redirect_capacity", stats.RedirectCapacity);
        yield return ("redirect_non_tcp", stats.RedirectNonTcp);
        yield return ("redirect_inject_failed", stats.RedirectInjectFailed);
        yield return ("redirect_expired", stats.RedirectExpired);
        yield return ("redirect_multi_nb", stats.RedirectMultiNb);
        yield return ("redirect_multi_l", stats.RedirectMultiL);
        yield return ("udp_created", stats.UdpCreated);
        yield return ("udp_ingress", stats.UdpIngress);
        yield return ("udp_return", stats.UdpReturn);
        yield return ("udp_blocked", stats.UdpBlocked);
        yield return ("udp_return_failed", stats.UdpReturnFailed);
        yield return ("udp_return_miss", stats.UdpReturnMiss);
        yield return ("udp_return_ambiguous", stats.UdpReturnAmbiguous);
        yield return ("udp_no_config", stats.UdpNoConfig);
        yield return ("udp_no_state", stats.UdpNoState);
        yield return ("udp_no_ingress", stats.UdpNoIngress);
        yield return ("udp_capacity", stats.UdpCapacity);
        yield return ("udp_fragmented", stats.UdpFragmented);
        yield return ("udp_inject_failed", stats.UdpInjectFailed);
        yield return ("udp_expired", stats.UdpExpired);
        yield return ("udp_multi_nb", stats.UdpMultiNb);
        yield return ("udp_multi_l", stats.UdpMultiL);
        yield return ("udp_return_miss_blocked", stats.UdpReturnMissBlocked);

        yield return ("inject_complete_calls", stats.InjectCompleteCalls);
        yield return ("inject_complete_failed", stats.InjectCompleteFailed);
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
