using WinForward.Userland.Configuration;
using WinForward.Userland.Forwarded;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Control;

/// <summary>
/// 当前驱动能执行的 forwarded 动作集，以及配置侧的提前校验。
/// </summary>
/// <remarks>
/// B4a 的驱动只执行 permit / block；redirect 规则会在 <c>ioctl_set_forwarded_blob</c> 被
/// <c>STATUS_NOT_SUPPORTED</c> 整块拒绝。等到控制器绑好 listener 再撞上这个错误，会留下一个
/// 半启动的驱动状态，所以在配置编译完、打开设备之前就拒绝，并在错误里指出是 B4b 的能力。
/// </remarks>
public static class ForwardedRuntimeSupport
{
    public static void EnsureExecutable(ForwardedPolicyTable? table)
    {
        if (table is null)
        {
            return;
        }

        for (int index = 0; index < table.Rules.Count; index++)
        {
            ForwardedPolicyRule rule = table.Rules[index];
            if (rule.Action != RuleAction.Redirect)
            {
                continue;
            }

            string note = string.IsNullOrEmpty(rule.Name) ? string.Empty : $"（规则名：{rule.Name}）";
            throw new ConfigurationException(
                $"forwarded.rules[{index}] 是 redirect：当前驱动（B4a）不执行 forwarded 重定向，" +
                $"下发会被 STATUS_NOT_SUPPORTED 拒绝。先改用 permit / block；DNAT 与回程属于 B4b。{note}");
        }
    }
}
