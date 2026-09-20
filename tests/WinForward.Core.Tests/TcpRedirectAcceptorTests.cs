using System.Net;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.Core.Tests.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

/// <summary>
/// The accept-loop attach contract (R7): a relay the store refuses to attach is discarded together
/// with its accepted connection and the session is torn down, so a refused attach can never leave a
/// half-open redirect registered.
/// </summary>
public sealed class TcpRedirectAcceptorTests
{
    [Fact]
    public async Task UnattachableRelayIsDiscardedAndTheSessionIsTornDown()
    {
        var table = new TcpRedirectTable();
        var logger = new RecordingRuntimeLogger();
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
}
