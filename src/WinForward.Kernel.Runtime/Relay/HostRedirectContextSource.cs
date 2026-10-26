using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace WinForward.Userland.Relay;

/// <summary>host 路径：<c>SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT</c>。</summary>
public sealed class HostRedirectContextSource : IRedirectContextSource
{
    public string Name => "host";

    public bool TryRead(Socket client, [NotNullWhen(true)] out RedirectDestination? destination, out string error)
    {
        ArgumentNullException.ThrowIfNull(client);
        destination = null;
        var rawContext = SocketIoctl.TryQueryRedirectContext(client, out error);
        if (rawContext is null)
        {
            return false;
        }

        if (rawContext.Length == 0)
        {
            error = "host redirect context 为空";
            return false;
        }

        return RedirectDestination.TryParse(rawContext, out destination, out error);
    }

    /// <summary>host UDP 的原始目的地随 datagram 从 <c>WSARecvMsg</c> 的 cmsg 到达，不能按 (client, listener) 四元组查询。</summary>
    public bool TryReadUdp(EndPoint client, IPEndPoint listener,
                           [NotNullWhen(true)] out RedirectDestination? destination, out string error)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(listener);
        destination = null;
        error = "host UDP 的原始目的地在每条 datagram 的 cmsg 里，无法按四元组查询；listener 侧见 WfpUdpContext";
        return false;
    }

}
