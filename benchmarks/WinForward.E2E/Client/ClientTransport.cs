using System.Net;
using System.Net.Sockets;
using WinForward.E2E.Client.Lanes;

namespace WinForward.E2E.Client;

/// <summary>
/// The one TCP dial the client uses: a direct connect when no proxy is configured, and a SOCKS5
/// CONNECT when one is.
/// </summary>
/// <remarks>
/// The direct path keeps <see cref="SocketOps"/>'s result vocabulary, where a failed connect is a
/// scheduled shortfall the arm records. The proxy path intentionally does not: a missing or broken
/// hop means the row's declared path is absent, so the connector throws and the arm fails.
/// </remarks>
internal static class ClientTransport
{
    internal static ValueTask<LaneOpenResult> TryConnectAsync(
        Socket socket,
        EndPoint target,
        Socks5Proxy? proxy,
        CancellationToken cancellationToken) =>
        proxy is null
            ? SocketOps.TryConnectAsync(socket, target, cancellationToken)
            : ConnectThroughProxyAsync(socket, proxy, target, cancellationToken);

    private static async ValueTask<LaneOpenResult> ConnectThroughProxyAsync(
        Socket socket,
        Socks5Proxy proxy,
        EndPoint target,
        CancellationToken cancellationToken)
    {
        await Socks5Connector.ConnectAsync(socket, proxy, target, cancellationToken).ConfigureAwait(false);
        return new LaneOpenResult(Ok: true, Error: null);
    }
}
