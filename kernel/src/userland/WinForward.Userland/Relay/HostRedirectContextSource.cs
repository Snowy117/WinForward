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
        byte[]? rawContext = SocketIoctl.TryQueryRedirectContext(client, out error);
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

    /// <summary>host UDP 的回程由 listener 用同一个 socket 完成，不使用 forwarded redirect 表。</summary>
    public bool TryReadUdp(EndPoint client, IPEndPoint listener,
                           [NotNullWhen(true)] out RedirectDestination? destination, out string error)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(listener);
        destination = null;
        error = "host UDP 中继不在本仓库";
        return false;
    }

}
