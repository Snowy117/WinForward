using WinForward.Userland.Configuration;
using WinForward.Userland.Forwarded;

namespace WinForward.Userland.Control;

/// <summary>
/// forwarded 动作集在执行前的校验点。B4b 起驱动执行 permit / block / redirect 三种动作，
/// 这里不再按动作拒绝；保留入口是因为调用方（CheckCommand / RunCommand）在打开设备之前
/// 需要一个稳定的校验缝，后续若出现「驱动不支持的新动作」仍从这里拒绝。
/// </summary>
public static class ForwardedRuntimeSupport
{
    public static void EnsureExecutable(ForwardedPolicyTable? table)
    {
        // redirect 的可执行性由 blob 解码（upstream_id 与 action 的组合）和控制器下发时的
        // 配置顺序共同保证；这里没有需要提前拒绝的动作。
        _ = table;
    }
}
