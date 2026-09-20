using System.Net;
using WinForward.Runtime.TcpRedirect;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// R1: the accept loop owns the lifetime CTS disposal — its finally runs when the linked shutdown
/// cancellation ends the loop first, so a retire that arrives afterwards must tolerate an already
/// disposed source. Retire and DisposeLifetime must also be idempotent in either order.
/// </summary>
public sealed class TcpRedirectSessionTests
{
    [Fact]
    public void RetireAfterTheLinkedShutdownAlreadyEndedTheLoopDoesNotThrow()
    {
        using var shutdown = new CancellationTokenSource();
        var session = CreateSession(shutdown.Token);
        var lifetime = session.Token;

        shutdown.Cancel();
        session.DisposeLifetime();
        session.Retire();

        Assert.True(session.IsRetired);
        Assert.True(lifetime.IsCancellationRequested);
    }

    [Fact]
    public void RetireCancelsTheLifetimeAndDisposeLifetimeIsIdempotent()
    {
        var session = CreateSession(CancellationToken.None);
        var lifetime = session.Token;

        session.Retire();
        session.Retire();

        Assert.True(session.IsRetired);
        Assert.True(lifetime.IsCancellationRequested);

        session.DisposeLifetime();
        session.DisposeLifetime();
    }

    [Fact]
    public void RetireAfterDisposeLifetimeLeavesTheDisposedSourceUntouched()
    {
        using var shutdown = new CancellationTokenSource();
        var session = CreateSession(shutdown.Token);
        var lifetime = session.Token;

        session.DisposeLifetime();
        session.Retire();

        Assert.False(lifetime.IsCancellationRequested);
    }

    private static TcpRedirectSession CreateSession(CancellationToken shutdown)
    {
        var association = TcpCoordinatorFakes.CreateHostAssociation(Endpoint.From(IPAddress.Loopback, 40000));
        return TcpCoordinatorFakes.CreateSession(association, new FakeListener(association.TranslatedListenerTuple), shutdown);
    }
}
