using System.Net;
using WinForward.Configuration;
using WinForward.Protocols;
using WinForward.Runtime.Socks5;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// The ordering contract of the scripted SOCKS5 server's ASSOCIATE reply: the reply counter is
/// published before the reply bytes become readable. A client's ASSOCIATE completes the moment it
/// reads those bytes, so a counter published after the write can lag a completed association by a
/// scheduling window — the window that made the assertions reading
/// <see cref="ScriptedSocks5UdpServer.AssociateReplyCount"/> racy under suite load.
/// </summary>
public sealed class ScriptedSocks5UdpServerOrderingTests
{
    private const int RelayPort = 42_000;

    [Fact]
    public async Task TheAssociateReplyCounterIsPublishedBeforeTheReplyBecomesReadable()
    {
        await using var server = new ScriptedSocks5UdpServer(new IPEndPoint(IPAddress.Loopback, RelayPort));
        var registry = new SelfTrafficRegistry();
        var factory = new Socks5UdpTransportFactory(registry, UdpFrameBuilder.DefaultMaximumEthernetFrame);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AssociateReplyWriteGate = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
        };

        var create = factory.CreateAsync(ProxyTarget.FromServer(server.Server), CancellationToken.None);
        Socks5UdpTransport? transport = null;
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await entered.Task.WaitAsync(budget.Token).ConfigureAwait(false);

            // The reply write is blocked, so no client can have read those bytes and completed an
            // association on them; the counter already reports the reply, which is the contract every
            // dependent assertion relies on.
            Assert.Equal(1, server.AssociateReplyCount);
        }
        finally
        {
            // Unblock on every path, then let the normal completion prove the gate was the only
            // thing delaying the association and that the release publishes no second reply.
            release.TrySetResult();
            try
            {
                transport = (Socks5UdpTransport)await create.ConfigureAwait(false);
            }
            finally
            {
                if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            }
        }

        Assert.Equal(1, server.AssociateReplyCount);
    }
}
