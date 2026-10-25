namespace WinForward.Userland.Policy;

/// <summary>
/// 一份策略：有序规则表 + 兜底动作。顺序即优先级，首个命中的规则生效。
/// </summary>
/// <remarks>
/// 兜底动作与规则表一起编译进 blob 的同一个头部：两者必须作为整体原子替换，
/// 拆成两个参数会在下发时留下不一致窗口。
/// </remarks>
public sealed class PolicyTable
{
    public PolicyTable(IEnumerable<PolicyRule> rules, RuleAction fallbackAction)
    {
        ArgumentNullException.ThrowIfNull(rules);

        Rules = [.. rules];
        FallbackAction = fallbackAction;
    }

    public IReadOnlyList<PolicyRule> Rules { get; }

    public RuleAction FallbackAction { get; }
}
