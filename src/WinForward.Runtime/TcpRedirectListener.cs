using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WinForward.Core;

namespace WinForward.Runtime;

[SupportedOSPlatform("windows")]
public sealed class TcpRedirectListenerFactory : ITcpRedirectListenerFactory
{
    public ValueTask<ITcpRedirectListener> CreateAsync(AddressFamilyKind addressFamily, CancellationToken cancellationToken)
    {
        var loopback = addressFamily == AddressFamilyKind.IPv4 ? IPAddress.Loopback : IPAddress.IPv6Loopback;
        var socket = new Socket(loopback.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(loopback, 0));
            socket.Listen(backlog: 16);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var localEndpoint = (IPEndPoint)socket.LocalEndPoint!;
        var translatedTuple = Endpoint.From(localEndpoint.Address, checked((ushort)localEndpoint.Port));
        return ValueTask.FromResult<ITcpRedirectListener>(new TcpRedirectListener(socket, translatedTuple));
    }
}

[SupportedOSPlatform("windows")]
internal sealed class TcpRedirectListener(Socket socket, Endpoint translatedTuple) : ITcpRedirectListener
{
    public Endpoint TranslatedTuple { get; } = translatedTuple;

    public async ValueTask<ITcpAcceptedConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        var accepted = await socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
        var remote = (IPEndPoint)accepted.RemoteEndPoint!;
        var remoteEndpoint = Endpoint.From(remote.Address, checked((ushort)remote.Port));
        return new TcpAcceptedConnection(accepted, remoteEndpoint);
    }

    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class TcpAcceptedConnection : ITcpAcceptedConnection
{
    private readonly Socket _socket;

    public TcpAcceptedConnection(Socket socket, Endpoint remoteEndPoint)
    {
        _socket = socket;
        RemoteEndPoint = remoteEndPoint;
    }

    public Endpoint RemoteEndPoint { get; }

    internal Socket Socket => _socket;

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
