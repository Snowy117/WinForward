using WinForward.Userland.Policy;

namespace WinForward.Userland.Configuration;

/// <summary>一份已解析的控制器配置：规则表，以及可选的上游 SOCKS5。</summary>
/// <remarks>
/// <see cref="Socks5"/> 为 null 表示直连 —— 与没有这个功能时的行为完全一致。
/// 中继不会在「配了上游但连不上」时退回这一侧：那会把代理失效伪装成成功。
/// </remarks>
public sealed class ControllerConfiguration
{
    public required PolicyTable Policy { get; init; }

    public Socks5Endpoint? Socks5 { get; init; }
}
