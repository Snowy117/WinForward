namespace WinForward.Userland.Policy;

/// <summary>
/// 一条规则。<see cref="Name"/> 只用于诊断，不落盘。
/// </summary>
public sealed class PolicyRule
{
    public string? Name { get; init; }

    public ProcessSelector? Process { get; init; }

    public RemoteNetwork? RemoteNetwork { get; init; }

    /// <summary>
    /// 地址族选择器。写网段时可以与网段的族不同（含 Any），编译期会拒绝「两者都具体却不一致」的组合。
    /// </summary>
    public RuleAddressFamily Family { get; init; } = RuleAddressFamily.Any;

    public RuleProtocol Protocol { get; init; } = RuleProtocol.Any;

    public PortRange RemotePorts { get; init; } = PortRange.Any;

    public RuleAction Action { get; init; } = RuleAction.Permit;

    public override string ToString() => Name ?? "(未命名规则)";
}
