using WinForward.Configuration;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// The per-flow redirect state: the association (redirect table entry), the local listener, the
/// self-traffic token, the SOCKS5 server, a scope-owned lifetime cancellation token, and the optional
/// relay/accept-loop handles. A top-level internal type (not nested on <see cref="TcpProxyCoordinator"/>)
/// so the accept/reset/relay modules can reference it without a circular dependency on the
/// coordinator itself.
/// </summary>
/// <remarks>
/// The lifetime is a nested <see cref="QuiescenceScope"/> linked to the store's scope token (D7):
/// <see cref="Retire"/> cancels it, <see cref="DisposeLifetimeAsync"/> drains it (seal + join + release the
/// CTS), and <see cref="Token"/> is the scope-owned token. <see cref="IsRetired"/> stays an explicit
/// owner-held admission flag (D1) because the scope has no seal-only transition: retiring must cancel
/// the accept loop immediately while the token stays readable until that loop ends (R1).
/// </remarks>
internal sealed class TcpRedirectSession(TcpRedirectAssociation association, ITcpRedirectListener listener, SelfTrafficRegistry.SelfTrafficToken selfTrafficToken, Socks5Server server, long flowGeneration, CancellationToken shutdown)
{
    private readonly QuiescenceScope _scope = new(shutdown);
    private int _retired;

    public TcpRedirectAssociation Association { get; } = association;
    public ITcpRedirectListener Listener { get; } = listener;
    public SelfTrafficRegistry.SelfTrafficToken SelfTrafficToken { get; } = selfTrafficToken;
    public Socks5Server Server { get; } = server;
    public long FlowGeneration { get; } = flowGeneration;
    public ITcpRelay? Relay { get; set; }
    public Task? AcceptLoop { get; set; }
    public CancellationToken Token => _scope.Token;
    public bool IsRetired => Volatile.Read(ref _retired) != 0;

    public void Retire()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0) return;
        // Cancel, never dispose: the accept loop (and the reset injector it drives) may still read
        // Token while it unwinds, so only the drain — invoked once the loop has ended — releases
        // the scope's CTS (R1).
        _scope.Cancel();
    }

    public ValueTask DisposeLifetimeAsync() => _scope.DisposeAsync();
}
