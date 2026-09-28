using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Core.Tests;

/// <summary>
/// Builds a real transport over a one-flow association pool, the way production composition does
/// (the pool rents the lease, the transport binds its relay socket in the lease's family). The
/// default mode is <see cref="UdpAssociationReuseMode.Off"/> so a test that does not care about
/// sharing sees exactly today's one-association-per-flow lifecycle.
/// </summary>
internal static class UdpTransportTestFactory
{
    internal static async ValueTask<UdpTransportHandle> CreateAsync(
        Socks5Server server,
        SelfTrafficRegistry registry,
        Func<AddressFamily, Socket>? socketFactory = null,
        Action<Socket>? disableUdpConnectionReset = null,
        int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame,
        int relayReceiveBufferBytes = Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize,
        UdpAssociationReuseMode mode = UdpAssociationReuseMode.Off,
        Func<Socks5Server, CancellationToken, ValueTask<Socks5ControlConnection>>? createControl = null,
        Socks5AddressCache? addressCache = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var pool = new UdpAssociationPool(registry, mode, addressCache, timeProvider, createControl: createControl);
        try
        {
            var lease = await pool.RentAsync(server, cancellationToken).ConfigureAwait(false);
            var transport = Socks5UdpTransport.Create(lease, registry, socketFactory, disableUdpConnectionReset, maximumFrameSize, relayReceiveBufferBytes);
            return new UdpTransportHandle(transport, pool);
        }
        catch
        {
            await pool.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// One transport plus the pool its association came from. Disposing releases the transport (which
/// releases its lease) before the pool, so a test can assert the same drain order production uses
/// without spelling it out at every call site.
/// </summary>
internal sealed class UdpTransportHandle(Socks5UdpTransport transport, UdpAssociationPool pool) : IAsyncDisposable
{
    public Socks5UdpTransport Transport { get; } = transport;

    public async ValueTask DisposeAsync()
    {
        await Transport.DisposeAsync().ConfigureAwait(false);
        await pool.DisposeAsync().ConfigureAwait(false);
    }
}
