namespace WinForward.Userland.Device;

/// <summary>
/// 一次 classify 的结论，数值逐项对应 <c>src/driver/probe_ioctl.h</c> 的 <c>probe_decision</c>。
/// </summary>
/// <remarks>新增只追加；协议版本不变时不允许改已有取值。</remarks>
public enum ProbeDecision : byte
{
    PermittedDisabled = 0,
    PermittedNoWriteRight = 1,
    PermittedNonTcp = 2,
    PermittedListenerPort = 3,
    PermittedConditionFlags = 4,
    PermittedRedirectState = 5,
    PermittedAlreadyRedirected = 6,
    PermittedSelfProcess = 7,
    PermittedNoRules = 8,
    PermittedUnsupported = 9,
    PermittedFallback = 10,
    PermittedRule = 11,
    BlockedRule = 12,
    BlockedFallback = 13,
    Redirected = 14,
    RedirectFailed = 15,
    ContextAllocFailed = 16,
    PermittedDeferredBlock = 17,
}
