using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Runtime.Socks5;
using Xunit;
using static WinForward.Core.Tests.Socks5TestServer;

namespace WinForward.Core.Tests;

/// <summary>
/// R3/B9 (task 09-18): the configured SOCKS5 endpoint is resolved once and cached; steady-state
/// connections consume the cached value with no DNS involvement, and a connection failure marks
/// the cache dirty so the next attempt re-resolves. Both TCP relay and UDP session setup share
/// one cache.
/// </summary>
public sealed class Socks5AddressCacheTests
{
    [Fact]
    public async Task SecondConnectConsumesTheCachedAddressWithoutResolvingAgain()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var server = new Socks5Server("test", "cached.socks.example", checked((ushort)endpoint.Port), Username: null, Password: null);
        var cache = new Socks5AddressCache();
        var resolutions = 0;
        ValueTask<IPAddress[]> Resolver(string _1, CancellationToken _2)
        {
            Interlocked.Increment(ref resolutions);
            return ValueTask.FromResult(new[] { IPAddress.Loopback });
        }

        var firstAccept = AcceptGreetingAsync(listener, CancellationToken.None);
        await using (var first = await Socks5ControlConnection.ConnectAsync(server, CancellationToken.None, resolveAddresses: Resolver, addressCache: cache))
        {
            // The first connection resolves and populates the cache.
            Assert.Equal(1, resolutions);
        }
        await firstAccept;

        var secondAccept = AcceptGreetingAsync(listener, CancellationToken.None);
        await using (var second = await Socks5ControlConnection.ConnectAsync(server, CancellationToken.None, resolveAddresses: Resolver, addressCache: cache))
        {
            // Steady state: the cache hit must bypass DNS entirely.
            Assert.Equal(1, resolutions);
        }
        await secondAccept;
    }

    [Fact]
    public async Task ConnectFailureMarksTheCachedEndpointDirtyForReResolution()
    {
        var server = new Socks5Server("test", "dead.socks.example", 1080, Username: null, Password: null);
        var cache = new Socks5AddressCache();
        var resolutions = 0;
        ValueTask<IPAddress[]> Resolver(string _1, CancellationToken _2)
        {
            Interlocked.Increment(ref resolutions);
            return ValueTask.FromResult(new[] { IPAddress.Loopback });
        }

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await Socks5ControlConnection.ConnectAsync(
                server,
                CancellationToken.None,
                resolveAddresses: Resolver,
                socketFactory: _ => throw new SocketException((int)SocketError.ConnectionRefused),
                maxAttempts: 1,
                addressCache: cache);
        });

        Assert.Equal(1, resolutions);
        Assert.False(cache.TryGet(server.Host, out _));

        // The next attempt re-resolves rather than reusing a dead cached address.
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await Socks5ControlConnection.ConnectAsync(
                server,
                CancellationToken.None,
                resolveAddresses: Resolver,
                socketFactory: _ => throw new SocketException((int)SocketError.ConnectionRefused),
                maxAttempts: 1,
                addressCache: cache);
        });
        Assert.Equal(2, resolutions);
    }
}
