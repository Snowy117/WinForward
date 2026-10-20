namespace WinForward.Userland.Policy;

/// <summary>跨语言契约里的容量上限。改这里必须同步内核侧同名常量与 spec。</summary>
public static class PolicyLimits
{
    /// <summary>
    /// 进程路径的 UTF-8 字节上限，不含结尾 NUL。
    /// </summary>
    /// <remarks>
    /// 内核侧对应 <c>max_process_path_length = 512</c>（含 NUL 的缓冲区），见
    /// <c>src/driver/probe_driver.h</c> 与
    /// <c>.trellis/tasks/10-20-wfp-driver-rules/research/findings.md</c>。
    /// 超过上限的路径在 classify 侧会被整条丢弃（<c>process_path_length = 0</c>），
    /// 规则从此静默不命中且不报错 —— 所以必须在这里拒绝，而不是让它进 blob。
    /// </remarks>
    public const int MaxProcessPathBytes = 511;
}
