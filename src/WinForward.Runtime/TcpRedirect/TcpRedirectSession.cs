using WinForward.Configuration;

namespace WinForward.Runtime.TcpRedirect;

/// <summary>
/// The per-flow redirect state: the association (redirect table entry), the local listener, the
/// self-traffic token, the SOCKS5 server, a linked lifetime cancellation token, and the optional
/// relay/accept-loop tasks. A top-level internal type (not nested on <see cref="TcpProxyCoordinator"/>)
/// so the accept/reset/relay modules can reference it without a circular dependency on the
/// coordinator itself.
/// </summary>
internal sealed class TcpRedirectSession(TcpRedirectAssociation association, ITcpRedirectListener listener, SelfTrafficRegistry.SelfTrafficToken selfTrafficToken, Socks5Server server, long flowGeneration, CancellationToken shutdown)
{
    public TcpRedirectAssociation Association { get; } = association;
    public ITcpRedirectListener Listener { get; } = listener;
    public SelfTrafficRegistry.SelfTrafficToken SelfTrafficToken { get; } = selfTrafficToken;
    public Socks5Server Server { get; } = server;
    public long FlowGeneration { get; } = flowGeneration;
    private CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
    private int _retired;
    private int _lifetimeDisposed;
    public ITcpRelay? Relay { get; set; }
    public Task? AcceptLoop { get; set; }
    public CancellationToken Token => Lifetime.Token;
    public bool IsRetired => Volatile.Read(ref _retired) != 0;

    public void Retire()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0 || Volatile.Read(ref _lifetimeDisposed) != 0) return;
        try
        {
            Lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The linked shutdown cancellation ended the accept loop, which owns the lifetime CTS
            // disposal, before this retire ran — the token is already cancelled either way.
        }
    }

    public void DisposeLifetime()
    {
        if (Interlocked.Exchange(ref _lifetimeDisposed, 1) == 0) Lifetime.Dispose();
    }
}
