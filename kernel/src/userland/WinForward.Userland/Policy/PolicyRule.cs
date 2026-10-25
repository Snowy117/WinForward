namespace WinForward.Userland.Policy;

/// <summary>
/// 一条规则。<see cref="Name"/> 只用于诊断，不落盘。
/// </summary>
/// <remarks>
/// 五个选择器都是集合：同一字段内是 OR、字段之间是 AND。<c>null</c> 表示不限制，
/// 显式空集合在编译期拒绝。规则顺序即优先级，一条配置规则仍然只编译成一条 blob 规则。
/// </remarks>
public sealed class PolicyRule
{
    public string? Name { get; init; }

    public IReadOnlyList<ProcessSelector>? Processes { get; init; }

    public IReadOnlyList<RemoteNetwork>? RemoteNetworks { get; init; }

    public IReadOnlyList<PortRange>? RemotePorts { get; init; }

    public IReadOnlyList<RuleProtocol>? Protocols { get; init; }

    public IReadOnlyList<RuleAddressFamily>? Families { get; init; }

    public RuleAction Action { get; init; } = RuleAction.Permit;

    public override string ToString() => Name ?? "(未命名规则)";
}
