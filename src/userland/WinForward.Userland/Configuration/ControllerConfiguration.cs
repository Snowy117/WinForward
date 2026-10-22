using WinForward.Userland.Forwarded;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Configuration;

/// <summary>
/// 一份已解析的控制器配置：host 规则表、可选的 forwarded 规则表，以及命名上游。
/// </summary>
/// <remarks>
/// <see cref="Socks5"/> 为 null 表示直连 —— 与没有这个功能时的行为完全一致。
/// 中继不会在「配了上游但连不上」时退回这一侧：那会把代理失效伪装成成功。
///
/// <see cref="Forwarded"/> 为 null 表示配置里没有 forwarded 段（旧配置），
/// 此时控制器行为与 B2 之前完全相同。<see cref="Upstreams"/> 含 `socks5Servers` 的命名上游；
/// 旧 `socks5`（若有）以 `default` 追加在最后，供 forwarded 规则的 target 引用；
/// <see cref="ForwardedUpstreams"/> 保存同一顺序的 id → endpoint 映射，供 B4 按 upstream_id 反查。
/// </remarks>
public sealed class ControllerConfiguration
{
    public required PolicyTable Policy { get; init; }

    public Socks5Endpoint? Socks5 { get; init; }

    /// <summary>可选的 forwarded 策略；null 表示没有 forwarded 段。</summary>
    public ForwardedPolicyTable? Forwarded { get; init; }

    /// <summary>
    /// 命名上游表。键大小写不敏感：先按 socks5Servers 的数组顺序，旧 socks5 段以
    /// default 追加在其后；同一个名字不会出现两次。
    /// </summary>
    public IReadOnlyDictionary<string, Socks5Endpoint> Upstreams { get; init; } =
        new Dictionary<string, Socks5Endpoint>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 有序上游表：索引位置即 blob 的 upstream_id，B4 只靠它就能从 id 找到 endpoint。
    /// </summary>
    public IReadOnlyList<ForwardedUpstream> ForwardedUpstreams { get; init; } = [];
}
