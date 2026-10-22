namespace WinForward.Userland.Control;

/// <summary>控制器报告行的纯格式化函数；不依赖设备，便于单测锁死字面格式。</summary>
internal static class ControllerReport
{
    /// <summary>forwarded 表就绪时的单行报告。</summary>
    internal static string Forwarded(int ruleCount, int upstreamCount, int blobBytes) =>
        $"controller-forwarded count={ruleCount} upstreams={upstreamCount} blob={blobBytes}";
}
