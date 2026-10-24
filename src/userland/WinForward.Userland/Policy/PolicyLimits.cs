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

    /// <summary>
    /// 一次换表能安装的 <c>ALE_AUTH_CONNECT</c> block filter 总数。
    /// </summary>
    /// <remarks>
    /// 对应 <c>src/driver/probe_ioctl.h</c> 的 <c>max_block_filters = 128</c>。驱动按同一公式
    /// 展开数组后逐条安装，超限时删除已建 filter 并整块拒绝；编译期先算一遍，避免把
    /// 注定失败的规则表下发到内核。
    /// </remarks>
    public const int MaxBlockFilters = 128;

    /// <summary>
    /// 规则 blob 的总字节上限。
    /// </summary>
    /// <remarks>对应 <c>src/driver/probe_ioctl.h</c> 的 <c>rule_blob_max_size = 64 * 1024</c>。</remarks>
    public const int RuleBlobMaxSize = 64 * 1024;
}
