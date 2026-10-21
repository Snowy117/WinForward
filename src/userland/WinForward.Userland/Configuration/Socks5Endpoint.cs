namespace WinForward.Userland.Configuration;

/// <summary>上游 SOCKS5 服务的地址：策略文件 <c>socks5</c> 段的产物。</summary>
/// <remarks>
/// 这里只保证「能拿去做 TCP 连接」：host 允许域名或 IP 字面量，解析交给
/// <see cref="Relay.Socks5Client"/>。端口区间在构造期拒绝，避免把 0 端口一路带到握手阶段才发现。
/// </remarks>
public sealed record Socks5Endpoint
{
    public Socks5Endpoint(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "SOCKS5 上游端口必须在 1..65535");
        }

        Host = host.Trim();
        Port = port;
    }

    public string Host { get; }

    public int Port { get; }

    public override string ToString() => $"{Host}:{Port}";
}
