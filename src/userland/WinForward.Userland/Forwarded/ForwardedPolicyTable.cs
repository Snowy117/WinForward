using WinForward.Userland.Policy;

namespace WinForward.Userland.Forwarded;

/// <summary>
/// 一份 forwarded 策略：有序规则表 + 兜底动作。顺序即优先级，首个命中的规则生效。
/// </summary>
/// <remarks>
/// 兜底动作与规则表一起编译进同一个 blob 头部：两者必须作为整体原子替换。
/// </remarks>
public sealed class ForwardedPolicyTable
{
    public ForwardedPolicyTable(IEnumerable<ForwardedPolicyRule> rules, RuleAction fallbackAction)
    {
        ArgumentNullException.ThrowIfNull(rules);

        Rules = rules.ToArray();
        FallbackAction = fallbackAction;
    }

    public IReadOnlyList<ForwardedPolicyRule> Rules { get; }

    public RuleAction FallbackAction { get; }
}
