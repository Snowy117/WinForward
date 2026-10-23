using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinForward.Userland.Device;

/// <summary>
/// 产品侧 ABI 结构的布局断言。数字与 <c>src/driver/probe_ioctl.h</c> 的 <c>static_assert</c> 相同；
/// 一致性测试会从头文件文本里读回这些数字，任何一边改动而另一边没跟上都会失败。
/// </summary>
/// <remarks>
/// <see cref="Verify"/> 在打开设备之前跑一次：布局对不上时立刻带着「哪个结构、哪个字段」报错，
/// 而不是让一次 DeviceIoControl 返回一个看不出原因的 Win32 错误码。
/// </remarks>
public static class DeviceLayout
{
    public const int ProbeConfigSize = 64;
    public const int ProbeStatsSize = 264;
    public const int ProbeRuleInfoSize = 32;
    public const int ProbeForwardedInfoSize = 32;
    public const int ProbeForwardedConfigSize = 28;
    public const int ProbeForwardedRedirectQuerySize = 24;
    public const int ProbeForwardedRedirectSize = 64;
    public const int ProbeForwardedStatsSize = 400;
    public const int ProbeEventSize = 64;
    public const int ProcessPathMeasurementSize = 520;
    public const int RedirectContextSize = 36;

    private static readonly object Gate = new();
    private static bool _verified;

    public static void Verify()
    {
        lock (Gate)
        {
            if (_verified)
            {
                return;
            }

            VerifyProbeConfig();
            VerifyProbeStats();
            VerifyProbeRuleInfo();
            VerifyProbeForwarded();
            VerifyProbeForwardedRedirect();
            VerifyProbeEvent();
            VerifyProcessPathMeasurement();
            VerifyRedirectContext();
            _verified = true;
        }
    }

    private static void VerifyProbeConfig()
    {
        CheckSize<ProbeConfig>(ProbeConfigSize);
        CheckOffset<ProbeConfig>(nameof(ProbeConfig.AbiVersion), 0);
        CheckOffset<ProbeConfig>(nameof(ProbeConfig.Enabled), 4);
        CheckOffset<ProbeConfig>(nameof(ProbeConfig.ListenerProcessId), 8);
        CheckOffset<ProbeConfig>(nameof(ProbeConfig.ListenerPort), 12);
        CheckOffset<ProbeConfig>(nameof(ProbeConfig.Reserved0), 14);
        CheckOffset<ProbeConfig>(nameof(ProbeConfig.SelfProcessCount), 16);
        CheckOffset<ProbeConfig>(nameof(ProbeConfig.SelfProcessIds), 20);
        CheckOffset<ProbeConfig>(nameof(ProbeConfig.Reserved), 52);
    }

    private static void VerifyProbeStats()
    {
        CheckSize<ProbeStats>(ProbeStatsSize);
        CheckOffset<ProbeStats>(nameof(ProbeStats.ClassifyCalls), 0);
        CheckOffset<ProbeStats>(nameof(ProbeStats.Permitted), 8);
        CheckOffset<ProbeStats>(nameof(ProbeStats.Blocked), 16);
        CheckOffset<ProbeStats>(nameof(ProbeStats.Redirected), 24);
        CheckOffset<ProbeStats>(nameof(ProbeStats.RulesReplaced), 32);
        CheckOffset<ProbeStats>(nameof(ProbeStats.RejectedBlobs), 40);
        CheckOffset<ProbeStats>(nameof(ProbeStats.ContextsAllocated), 48);
        CheckOffset<ProbeStats>(nameof(ProbeStats.ContextAllocFailures), 56);
        CheckOffset<ProbeStats>(nameof(ProbeStats.ApplyFailures), 64);
        CheckOffset<ProbeStats>(nameof(ProbeStats.RedirectFailures), 72);
        CheckOffset<ProbeStats>(nameof(ProbeStats.PermittedDisabled), 80);
        CheckOffset<ProbeStats>(nameof(ProbeStats.PermittedNoRules), 88);
        CheckOffset<ProbeStats>(nameof(ProbeStats.PermittedNoWriteRight), 96);
        CheckOffset<ProbeStats>(nameof(ProbeStats.PermittedUnsupported), 104);
        CheckOffset<ProbeStats>(nameof(ProbeStats.PermittedNonTcp), 112);
        CheckOffset<ProbeStats>(nameof(ProbeStats.PermittedListenerPort), 120);
        CheckOffset<ProbeStats>(nameof(ProbeStats.PermittedRedirectState), 128);
        CheckOffset<ProbeStats>(nameof(ProbeStats.PermittedConditionFlags), 136);
        CheckOffset<ProbeStats>(nameof(ProbeStats.PermittedAlreadyRedirected), 144);
        CheckOffset<ProbeStats>(nameof(ProbeStats.PermittedSelfProcess), 152);
        CheckOffset<ProbeStats>(nameof(ProbeStats.MatchedPermit), 160);
        CheckOffset<ProbeStats>(nameof(ProbeStats.MatchedBlock), 168);
        CheckOffset<ProbeStats>(nameof(ProbeStats.MatchedRedirect), 176);
        CheckOffset<ProbeStats>(nameof(ProbeStats.FallbackPermit), 184);
        CheckOffset<ProbeStats>(nameof(ProbeStats.FallbackBlock), 192);
        CheckOffset<ProbeStats>(nameof(ProbeStats.FallbackRedirect), 200);
        CheckOffset<ProbeStats>(nameof(ProbeStats.DeferredBlocks), 208);
        CheckOffset<ProbeStats>(nameof(ProbeStats.ProcessPathMetadataSeen), 216);
        CheckOffset<ProbeStats>(nameof(ProbeStats.UdpClassifyCalls), 224);
        CheckOffset<ProbeStats>(nameof(ProbeStats.UdpPermitted), 232);
        CheckOffset<ProbeStats>(nameof(ProbeStats.UdpBlocked), 240);
        CheckOffset<ProbeStats>(nameof(ProbeStats.UdpRedirected), 248);
        CheckOffset<ProbeStats>(nameof(ProbeStats.LastStatus), 256);
        CheckOffset<ProbeStats>(nameof(ProbeStats.AbiVersion), 260);
    }

    private static void VerifyProbeRuleInfo()
    {
        CheckSize<ProbeRuleInfo>(ProbeRuleInfoSize);
        CheckOffset<ProbeRuleInfo>(nameof(ProbeRuleInfo.AbiVersion), 0);
        CheckOffset<ProbeRuleInfo>(nameof(ProbeRuleInfo.Generation), 4);
        CheckOffset<ProbeRuleInfo>(nameof(ProbeRuleInfo.Installed), 8);
        CheckOffset<ProbeRuleInfo>(nameof(ProbeRuleInfo.RuleCount), 12);
        CheckOffset<ProbeRuleInfo>(nameof(ProbeRuleInfo.FallbackAction), 16);
        CheckOffset<ProbeRuleInfo>(nameof(ProbeRuleInfo.TotalSize), 20);
        CheckOffset<ProbeRuleInfo>(nameof(ProbeRuleInfo.ProcessPoolSize), 24);
        CheckOffset<ProbeRuleInfo>(nameof(ProbeRuleInfo.NetworkPoolCount), 28);
    }

    private static void VerifyProbeForwarded()
    {
        CheckSize<ProbeForwardedInfo>(ProbeForwardedInfoSize);
        CheckOffset<ProbeForwardedInfo>(nameof(ProbeForwardedInfo.AbiVersion), 0);
        CheckOffset<ProbeForwardedInfo>(nameof(ProbeForwardedInfo.Generation), 4);
        CheckOffset<ProbeForwardedInfo>(nameof(ProbeForwardedInfo.Installed), 8);
        CheckOffset<ProbeForwardedInfo>(nameof(ProbeForwardedInfo.RuleCount), 12);
        CheckOffset<ProbeForwardedInfo>(nameof(ProbeForwardedInfo.FallbackAction), 16);
        CheckOffset<ProbeForwardedInfo>(nameof(ProbeForwardedInfo.TotalSize), 20);
        CheckOffset<ProbeForwardedInfo>(nameof(ProbeForwardedInfo.NetworkPoolCount), 24);
        CheckOffset<ProbeForwardedInfo>(nameof(ProbeForwardedInfo.Reserved0), 28);

        CheckSize<ProbeForwardedStats>(ProbeForwardedStatsSize);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.ClassifyCalls), 0);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.InboundFrames), 8);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.OutboundFrames), 16);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.Permitted), 24);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.Blocked), 32);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.Matched), 40);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.MatchedPermit), 48);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.MatchedBlock), 56);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.Fallback), 64);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.FallbackPermit), 72);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.FallbackBlock), 80);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.SelfInjected), 88);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.NoTable), 96);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.Malformed), 104);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.NonIp), 112);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.NoWriteRight), 120);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RejectedBlobs), 128);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.TablesReplaced), 136);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.MatchedRedirect), 144);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.Redirected), 152);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RedirectCreated), 160);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RedirectIngress), 168);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RedirectReturn), 176);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RedirectBlocked), 184);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RedirectReturnFailed), 192);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RedirectNoConfig), 200);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RedirectNoState), 208);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RedirectCapacity), 216);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RedirectNonTcp), 224);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RedirectInjectFailed), 232);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RedirectExpired), 240);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RedirectReturnMiss), 248);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RedirectMultiNb), 256);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.InjectCompleteCalls), 264);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.InjectCompleteFailed), 272);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.UdpCreated), 280);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.UdpIngress), 288);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.UdpReturn), 296);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.UdpBlocked), 304);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.UdpReturnFailed), 312);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.UdpReturnMiss), 320);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.UdpNoConfig), 328);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.UdpNoState), 336);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.UdpCapacity), 344);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.UdpFragmented), 352);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.UdpInjectFailed), 360);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.UdpExpired), 368);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.UdpMultiNb), 376);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RedirectActive), 384);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.RedirectHighWater), 388);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.LastStatus), 392);
        CheckOffset<ProbeForwardedStats>(nameof(ProbeForwardedStats.AbiVersion), 396);
    }

    private static void VerifyProbeForwardedRedirect()
    {
        CheckSize<ProbeForwardedConfig>(ProbeForwardedConfigSize);
        CheckOffset<ProbeForwardedConfig>(nameof(ProbeForwardedConfig.Enabled), 0);
        CheckOffset<ProbeForwardedConfig>(nameof(ProbeForwardedConfig.ListenerPort), 4);
        CheckOffset<ProbeForwardedConfig>(nameof(ProbeForwardedConfig.Family), 6);
        CheckOffset<ProbeForwardedConfig>(nameof(ProbeForwardedConfig.Reserved0), 7);
        CheckOffset<ProbeForwardedConfig>(nameof(ProbeForwardedConfig.ListenerAddress), 8);
        CheckOffset<ProbeForwardedConfig>(nameof(ProbeForwardedConfig.Reserved1), 24);

        CheckSize<ProbeForwardedRedirectQuery>(ProbeForwardedRedirectQuerySize);
        CheckOffset<ProbeForwardedRedirectQuery>(nameof(ProbeForwardedRedirectQuery.ListenerPort), 0);
        CheckOffset<ProbeForwardedRedirectQuery>(nameof(ProbeForwardedRedirectQuery.Family), 2);
        CheckOffset<ProbeForwardedRedirectQuery>(nameof(ProbeForwardedRedirectQuery.Reserved0), 3);
        CheckOffset<ProbeForwardedRedirectQuery>(nameof(ProbeForwardedRedirectQuery.ClientAddress), 4);
        CheckOffset<ProbeForwardedRedirectQuery>(nameof(ProbeForwardedRedirectQuery.ClientPort), 20);
        CheckOffset<ProbeForwardedRedirectQuery>(nameof(ProbeForwardedRedirectQuery.Protocol), 22);
        CheckOffset<ProbeForwardedRedirectQuery>(nameof(ProbeForwardedRedirectQuery.Reserved1), 23);

        CheckSize<ProbeForwardedRedirect>(ProbeForwardedRedirectSize);
        CheckOffset<ProbeForwardedRedirect>(nameof(ProbeForwardedRedirect.Magic), 0);
        CheckOffset<ProbeForwardedRedirect>(nameof(ProbeForwardedRedirect.Version), 4);
        CheckOffset<ProbeForwardedRedirect>(nameof(ProbeForwardedRedirect.Family), 6);
        CheckOffset<ProbeForwardedRedirect>(nameof(ProbeForwardedRedirect.Protocol), 7);
        CheckOffset<ProbeForwardedRedirect>(nameof(ProbeForwardedRedirect.ClientAddress), 8);
        CheckOffset<ProbeForwardedRedirect>(nameof(ProbeForwardedRedirect.ClientPort), 24);
        CheckOffset<ProbeForwardedRedirect>(nameof(ProbeForwardedRedirect.ServerAddress), 26);
        CheckOffset<ProbeForwardedRedirect>(nameof(ProbeForwardedRedirect.ServerPort), 42);
        CheckOffset<ProbeForwardedRedirect>(nameof(ProbeForwardedRedirect.ListenerPort), 44);
        CheckOffset<ProbeForwardedRedirect>(nameof(ProbeForwardedRedirect.Reserved0), 46);
        CheckOffset<ProbeForwardedRedirect>(nameof(ProbeForwardedRedirect.IngressIfIndex), 48);
        CheckOffset<ProbeForwardedRedirect>(nameof(ProbeForwardedRedirect.UpstreamId), 52);
        CheckOffset<ProbeForwardedRedirect>(nameof(ProbeForwardedRedirect.RuleIndex), 56);
        CheckOffset<ProbeForwardedRedirect>(nameof(ProbeForwardedRedirect.State), 60);
    }

    private static void VerifyProbeEvent()
    {
        CheckSize<ProbeEvent>(ProbeEventSize);
        CheckOffset<ProbeEvent>(nameof(ProbeEvent.Timestamp100ns), 0);
        CheckOffset<ProbeEvent>(nameof(ProbeEvent.Sequence), 8);
        CheckOffset<ProbeEvent>(nameof(ProbeEvent.ProcessId), 12);
        CheckOffset<ProbeEvent>(nameof(ProbeEvent.RemoteAddress), 16);
        CheckOffset<ProbeEvent>(nameof(ProbeEvent.LocalAddress), 32);
        CheckOffset<ProbeEvent>(nameof(ProbeEvent.RemotePort), 48);
        CheckOffset<ProbeEvent>(nameof(ProbeEvent.LocalPort), 50);
        CheckOffset<ProbeEvent>(nameof(ProbeEvent.RedirectPort), 52);
        CheckOffset<ProbeEvent>(nameof(ProbeEvent.Decision), 54);
        CheckOffset<ProbeEvent>(nameof(ProbeEvent.Family), 55);
        CheckOffset<ProbeEvent>(nameof(ProbeEvent.Action), 56);
        CheckOffset<ProbeEvent>(nameof(ProbeEvent.Branch), 57);
        CheckOffset<ProbeEvent>(nameof(ProbeEvent.Protocol), 58);
        CheckOffset<ProbeEvent>(nameof(ProbeEvent.Reserved0), 59);
        CheckOffset<ProbeEvent>(nameof(ProbeEvent.MatchedRule), 60);
    }

    private static void VerifyProcessPathMeasurement()
    {
        CheckSize<ProcessPathMeasurement>(ProcessPathMeasurementSize);
        CheckOffset<ProcessPathMeasurement>(nameof(ProcessPathMeasurement.Utf8Length), 0);
        CheckOffset<ProcessPathMeasurement>(nameof(ProcessPathMeasurement.Reserved), 4);
        CheckOffset<ProcessPathMeasurement>(nameof(ProcessPathMeasurement.Utf8), 8);
    }

    private static void VerifyRedirectContext()
    {
        CheckSize<RedirectContext>(RedirectContextSize);
        CheckOffset<RedirectContext>(nameof(RedirectContext.Magic), 0);
        CheckOffset<RedirectContext>(nameof(RedirectContext.RuleIndex), 4);
        CheckOffset<RedirectContext>(nameof(RedirectContext.ProcessId), 8);
        CheckOffset<RedirectContext>(nameof(RedirectContext.Version), 12);
        CheckOffset<RedirectContext>(nameof(RedirectContext.OriginalPort), 14);
        CheckOffset<RedirectContext>(nameof(RedirectContext.Family), 16);
        CheckOffset<RedirectContext>(nameof(RedirectContext.Protocol), 17);
        CheckOffset<RedirectContext>(nameof(RedirectContext.Reserved), 18);
        CheckOffset<RedirectContext>(nameof(RedirectContext.OriginalAddress), 20);
    }

    private static void CheckSize<T>(int expected) where T : unmanaged
    {
        int managed = Unsafe.SizeOf<T>();
        int marshalled = Marshal.SizeOf<T>();
        if (managed != expected || marshalled != expected)
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name} 布局不一致：期望 {expected} 字节，Unsafe.SizeOf={managed}，Marshal.SizeOf={marshalled}");
        }
    }

    private static void CheckOffset<T>(string field, int expected) where T : unmanaged
    {
        long actual = (long)Marshal.OffsetOf<T>(field);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name}.{field} 偏移不一致：期望 {expected}，实际 {actual}");
        }
    }
}
