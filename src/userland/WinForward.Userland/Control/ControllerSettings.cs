using WinForward.Userland.Configuration;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Control;

/// <summary>启动控制器需要的东西：一份已编译的规则表，以及报告输出去向。</summary>
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

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(3);
}
