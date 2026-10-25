namespace WinForward.Userland.Control;

/// <summary>控制器报告行的纯格式化函数；不依赖设备，便于单测锁死字面格式。</summary>
internal static class ControllerReport
{
    /// <summary>forwarded 表就绪时的单行报告。</summary>
    internal static string Forwarded(int ruleCount, int upstreamCount, int blobBytes) =>
        $"controller-forwarded count={ruleCount} upstreams={upstreamCount} blob={blobBytes}";

    /// <summary>forwarded 表经 IOCTL 下发并回读校验之后的一行报告。</summary>
    internal static string ForwardedInstalled(uint generation, uint ruleCount, uint fallbackAction, int blobBytes) =>
        $"controller-forwarded-installed generation={generation} count={ruleCount} " +
        $"fallback={fallbackAction} blob={blobBytes}";
}
