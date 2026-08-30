using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WinForward.Core;

namespace WinForward.Runtime.TcpRedirect;

[SupportedOSPlatform("windows")]
public sealed class TcpRedirectListenerFactory : ITcpRedirectListenerFactory
{
    public ValueTask<ITcpRedirectListener> CreateAsync(AddressFamilyKind addressFamily, CancellationToken cancellationToken)
    {
        // The listener binds 0.0.0.0 (all interfaces) on an ephemeral port. The official
        // WinpkFilter local_redirect pattern rewrites the client SYN's destination from the
        // original server to the CLIENT's own address + the proxy port (IP swap), so the
        // redirected packet arrives at the client's local IP on the proxy port and must be
        // accepted on any interface, not just loopback.
        var bindAddress = addressFamily == AddressFamilyKind.IPv4 ? IPAddress.Any : IPAddress.IPv6Any;
        var socket = new Socket(bindAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(bindAddress, 0));
            socket.Listen(backlog: 64);
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
        // The accepted leg is a byte pipe: Nagle x delayed-ACK would stall small proxied
        // writes 40-200 ms (X4), so TCP_NODELAY goes on immediately after accept.
        accepted.NoDelay = true;
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
