using System.Diagnostics.CodeAnalysis;
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
}
