using System.Net;
using WinForward.Configuration;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.UdpAssociationFakes;

namespace WinForward.Core.Tests;

/// <summary>
/// The ordering contract of the scripted SOCKS5 server's ASSOCIATE reply: the reply counter is
/// published before the reply bytes become readable. A client's rent completes the moment it reads
/// those bytes, so a counter published after the write can lag a completed rent by a scheduling
/// window — the window that made the assertions reading
/// <see cref="ScriptedSocks5UdpServer.AssociateReplyCount"/> racy under suite load.
/// </summary>
public sealed class ScriptedSocks5UdpServerOrderingTests
{
    private const int FirstRelayPort = 42_000;

    [Fact]
    public async Task TheAssociateReplyCounterIsPublishedBeforeTheReplyBecomesReadable()
    {
        await using var server = new ScriptedSocks5UdpServer(new IPEndPoint(IPAddress.Loopback, FirstRelayPort));
        await using var pool = CreatePool(UdpAssociationReuseMode.Auto);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AssociateReplyWriteGate = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
        };

        var rent = pool.RentAsync(server.Server, CancellationToken.None);
        UdpAssociationLease? lease = null;
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await entered.Task.WaitAsync(budget.Token).ConfigureAwait(false);

            // The reply write is blocked, so no client can have read those bytes and completed a
            // rent on them; the counter already reports the reply, which is the contract every
            // dependent assertion relies on.
            Assert.Equal(1, server.AssociateReplyCount);
        }
        finally
        {
            // Unblock on every path, then let the normal completion prove the gate was the only
            // thing delaying the rent and that the release publishes no second reply.
            release.TrySetResult();
            try
            {
                lease = await rent.ConfigureAwait(false);
            }
            finally
            {
                if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
            }
        }

        Assert.Equal(1, server.AssociateReplyCount);
    }
}
