using System.Net;
using WinForward.Core;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;
using static WinForward.TestSupport.TcpCoordinatorFakes;

namespace WinForward.Runtime.TcpRedirect.Tests;

/// <summary>
/// The accept-loop attach contract: a relay the store refuses to attach is discarded together
/// with its accepted connection and the session is torn down, so a refused attach can never leave a
/// half-open redirect registered.
/// </summary>
public sealed class TcpRedirectAcceptorTests
{
    [Fact]
    public async Task UnattachableRelayIsDiscardedAndTheSessionIsTornDown()
    {
        var table = new TcpRedirectTable();
        var logger = new RecordingLogger();
        var store = new TcpRedirectSessionStore(table, logger, capacity: 8, TimeProvider.System);
        var listener = new FakeListener(Endpoint.From(IPAddress.Loopback, 40_000));
        var association = CreateHostAssociation(listener.TranslatedTuple);
        var session = CreateSession(association, listener);
        Assert.NotNull(store.TryRegister(session));
        var relayFactory = new CompletableRelayFactory();
        var acceptor = new TcpRedirectAcceptor(
            relayFactory,
            logger,
            new ClientResetInjector(new FakeInjector(), logger, store.TearDownSessionAsync, store.FailAssociationAsync),
            tryAttachRelay: static (_, _) => false,
            tearDownSession: store.TearDownSessionAsync);

        var accepted = new FakeAcceptedConnection(association.AcceptedPeerEndpoint);
        await listener.AcceptChannel.Writer.WriteAsync(accepted);

        await acceptor.RunAcceptLoopAsync(session);

        Assert.Equal(0, store.SessionCount);
        Assert.Equal(0, table.Count);
        Assert.True(listener.IsDisposed);
        Assert.True(accepted.IsDisposed);
        Assert.True(relayFactory.Relay is { IsDisposed: true });
    }

    [Fact]
    public async Task LinkLocalPeerWhoseZoneOnlyTheSocketKnowsStillEstablishesTheRelay()
    {
        var listener = new FakeListener(Endpoint.From(IPAddress.IPv6Any, 42_000));
        var association = CreateLinkLocalHostAssociation(listener.TranslatedTuple);
        var session = CreateSession(association, listener);
        var logger = new RecordingLogger();
        var relayFactory = new CompletableRelayFactory();
        var acceptor = new TcpRedirectAcceptor(
            relayFactory,
            logger,
            new ClientResetInjector(new FakeInjector(), logger, static _ => ValueTask.CompletedTask, static _ => ValueTask.CompletedTask),
            tryAttachRelay: static (_, _) => true,
            tearDownSession: static _ => ValueTask.CompletedTask);

        var accepted = new FakeAcceptedConnection(Endpoint.From(IPAddress.Parse("fe80::215:5dff:fe03:728b%26"), association.AcceptedPeerEndpoint.Port));
        Assert.NotEqual(association.AcceptedPeerEndpoint, accepted.RemoteEndPoint);
        await listener.AcceptChannel.Writer.WriteAsync(accepted, CancellationToken.None);
        var acceptLoop = acceptor.RunAcceptLoopAsync(session);

        await WaitForAsync(() => relayFactory.Relay is not null || accepted.IsDisposed);

        Assert.NotNull(relayFactory.Relay);
        Assert.Empty(logger.Events.Where(recorded => string.Equals(recorded.Name, "tcp.redirect.unrelatedPeer", StringComparison.Ordinal)));
        Assert.False(accepted.IsDisposed);
        relayFactory.Relay!.Complete();
        session.Retire();
        await acceptLoop;
    }

    private static TcpRedirectAssociation CreateLinkLocalHostAssociation(Endpoint translatedListenerTuple)
    {
        var peer = IPAddress.Parse("fe80::215:5dff:fe03:728b");
        var key = FlowKey.Create(Endpoint.From(peer, 52_840), Endpoint.From(peer, 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        return new TcpRedirectAssociation(key, key.Remote, 0x1234, translatedListenerTuple, forwardLocalAddress: null, 1, DateTimeOffset.UtcNow);
    }
}
