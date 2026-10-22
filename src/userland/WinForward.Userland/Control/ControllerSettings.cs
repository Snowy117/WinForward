using WinForward.Userland.Configuration;
using WinForward.Userland.Forwarded;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Control;

/// <summary>
/// 启动控制器需要的东西：一份已编译的 host 规则表、可选的 forwarded 表，以及报告输出去向。
/// </summary>
/// <remarks>
/// <see cref="Forwarded"/> / <see cref="ForwardedBlob"/> 为 null 表示配置里没有 forwarded 段；
/// B2 只把 forwarded 的编译结果带到控制器，真正的下发（IOCTL / redirect 表）属于 B4；
/// <see cref="ForwardedUpstreams"/> 与 <see cref="Configuration.ControllerConfiguration.ForwardedUpstreams"/> 是同一张有序表。
/// </remarks>
public sealed class ControllerSettings
{
    public required PolicyTable Policy { get; init; }

    public required byte[] RuleBlob { get; init; }

    /// <summary>就绪、下发结果与会话摘要都写到这里（同时由调用方决定是否落盘）。</summary>
    public TextWriter Report { get; init; } = TextWriter.Null;
    /// <summary>
    /// 是否把原连接的 redirect records 挂到中继的出站 socket。true（默认）走驱动闸 2；
    /// false 时不带 records，靠 self-PID 闸（闸 4）排除自流量。
    /// </summary>
    public bool AttachRedirectRecords { get; init; } = true;

    /// <summary>上游 SOCKS5；null（默认）表示直连，行为与没有这个功能时完全一致。</summary>
    public Socks5Endpoint? Socks5 { get; init; }

    /// <summary>可选的 forwarded 策略；null 表示没有 forwarded 段。</summary>
    public ForwardedPolicyTable? Forwarded { get; init; }

    /// <summary>forwarded 表编译出的字节流；null 表示没有 forwarded 段，B2 不会下发它。</summary>
    public byte[]? ForwardedBlob { get; init; }

    /// <summary>命名上游表，与 <see cref="Configuration.ControllerConfiguration.Upstreams"/> 同源。</summary>
    public IReadOnlyDictionary<string, Socks5Endpoint> Upstreams { get; init; } =
        new Dictionary<string, Socks5Endpoint>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 有序上游表，与 <see cref="Configuration.ControllerConfiguration.ForwardedUpstreams"/> 同源；
    /// B4 用 id 从这里取 endpoint，不依赖 <see cref="Upstreams"/> 的枚举顺序。
    /// </summary>
    public IReadOnlyList<ForwardedUpstream> ForwardedUpstreams { get; init; } = [];

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(3);
}
