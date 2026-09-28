using System.Net;
using WinForward.Runtime.TcpRedirect;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// R1: the accept loop owns the lifetime drain — its finally runs when the linked shutdown
/// cancellation ends the loop first, so a retire that arrives afterwards must tolerate an already
/// drained (and released) source. Retire and the lifetime drain must also be idempotent in either
/// order.
/// </summary>
public sealed class TcpRedirectSessionTests
{
    [Fact]
    public async Task RetireAfterTheLinkedShutdownAlreadyEndedTheLoopDoesNotThrow()
    {
        using var shutdown = new CancellationTokenSource();
        var session = CreateSession(shutdown.Token);
        var lifetime = session.Token;

        await shutdown.CancelAsync();
        await session.DisposeLifetimeAsync();
        session.Retire();

        Assert.True(session.IsRetired);
        Assert.True(lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task RetireCancelsTheLifetimeAndTheDrainIsIdempotent()
    {
        var session = CreateSession(CancellationToken.None);
        var lifetime = session.Token;

        session.Retire();
        session.Retire();

        Assert.True(session.IsRetired);
        Assert.True(lifetime.IsCancellationRequested);

        await session.DisposeLifetimeAsync();
        await session.DisposeLifetimeAsync();
    }

    [Fact]
    public async Task LateRetireAfterTheDrainedLifetimeWasReleasedIsHarmless()
    {
        using var shutdown = new CancellationTokenSource();
        var session = CreateSession(shutdown.Token);
        var lifetime = session.Token;

        // The drain cancels the owned token and then releases the CTS, so the token stays
        // cancelled-readable and a retire that arrives afterwards must not touch the source again.
        await session.DisposeLifetimeAsync();
        session.Retire();

        Assert.True(lifetime.IsCancellationRequested);
        Assert.True(session.IsRetired);
    }

    private static TcpRedirectSession CreateSession(CancellationToken shutdown)
    {
        var association = TcpCoordinatorFakes.CreateHostAssociation(Endpoint.From(IPAddress.Loopback, 40000));
        return TcpCoordinatorFakes.CreateSession(association, new FakeListener(association.TranslatedListenerTuple), shutdown);
    }
}
