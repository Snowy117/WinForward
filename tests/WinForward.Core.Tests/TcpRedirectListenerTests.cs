using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WinForward.Core;
using WinForward.Runtime.TcpRedirect;
using Xunit;

namespace WinForward.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class TcpRedirectListenerTests
{
    [Fact]
    public async Task AcceptedSocketDisablesNagle()
    {
        // X4: the accepted leg is a byte pipe; Nagle x delayed-ACK would stall small proxied
        // writes 40-200 ms, so the accepted socket must leave TCP_NODELAY on.
        var factory = new TcpRedirectListenerFactory();
        await using var listener = await factory.CreateAsync(AddressFamilyKind.IPv4, CancellationToken.None);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, (int)listener.TranslatedTuple.Port);

        var accepted = await listener.AcceptAsync(CancellationToken.None);

        var connection = Assert.IsType<TcpAcceptedConnection>(accepted);
        Assert.True(connection.Socket.NoDelay);
    }
}
