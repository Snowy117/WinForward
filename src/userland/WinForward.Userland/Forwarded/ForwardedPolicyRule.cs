using WinForward.Userland.Policy;

namespace WinForward.Userland.Forwarded;

/// <summary>
/// 一条 Forwarded 规则。<see cref="Name"/> 只用于诊断，不落盘。
/// </summary>
/// <remarks>
/// 入口适配器与上游用 null 表达「不限 / 无」，编译期分别写成 0xFFFFFFFF 哨兵；
/// 0 是合法的具体适配器编号，不能用它表达「任意」。
/// </remarks>
public sealed class ForwardedPolicyRule
{
    public string? Name { get; init; }

    /// <summary>入口适配器编号；null 表示任意入口。</summary>
    public uint? IngressAdapterId { get; init; }

    /// <summary>客户端源地址网段；null 表示不限制。</summary>
    public RemoteNetwork? ClientNetwork { get; init; }

    /// <summary>目的地址网段；null 表示不限制。</summary>
    public RemoteNetwork? RemoteNetwork { get; init; }

    /// <summary>地址族选择器；具体族必须与两个网段的族一致。</summary>
    public RuleAddressFamily Family { get; init; } = RuleAddressFamily.Any;

    public RuleProtocol Protocol { get; init; } = RuleProtocol.Any;

    public PortRange RemotePorts { get; init; } = PortRange.Any;

    public RuleAction Action { get; init; } = RuleAction.Permit;

    /// <summary>redirect 必填，指向 SOCKS5 上游；非 redirect 必须为 null。</summary>
    public uint? UpstreamId { get; init; }

    public override string ToString() => Name ?? "(未命名规则)";
}
