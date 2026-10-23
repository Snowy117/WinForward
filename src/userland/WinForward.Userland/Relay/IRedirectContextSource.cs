using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace WinForward.Userland.Relay;

/// <summary>
/// 中继接受连接后读回原始目的地的接缝。host 路径读 WFP redirect context，
/// forwarded 路径查驱动 redirect 表；RelayServer 依次尝试，全部失败就拒绝连接。
/// </summary>
public interface IRedirectContextSource
{
    string Name { get; }

    bool TryRead(Socket client, [NotNullWhen(true)] out RedirectDestination? destination, out string error);

    /// <summary>
    /// UDP listener 收到 datagram 后读回原始目的地。client 是 datagram 的源地址，listener 是收到
    /// 它的本地端点（L + 端口）；同一个端口绑多个 L 时，listener 地址决定查哪条入口映射。
    /// </summary>
    bool TryReadUdp(EndPoint client, IPEndPoint listener, [NotNullWhen(true)] out RedirectDestination? destination,
                    out string error);

}
