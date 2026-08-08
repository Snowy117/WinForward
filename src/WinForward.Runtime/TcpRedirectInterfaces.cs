using WinForward.Core;

namespace WinForward.Runtime;

/// <summary>
/// Creates the local TCP listener that redirected client connections arrive on. The concrete
/// implementation (8c) binds a real socket and returns its translated tuple; a fake (8b tests)
/// returns a deterministic tuple. The factory is the single seam that keeps the coordinator
/// hardware-independent and testable without real sockets or NDISAPI reinjection.
/// </summary>
public interface ITcpRedirectListenerFactory
{
    ValueTask<ITcpRedirectListener> CreateAsync(AddressFamilyKind addressFamily, CancellationToken cancellationToken);
}

/// <summary>
/// A local TCP listener for the transparent-redirect leg. The <see cref="TranslatedTuple"/> is the
/// local (address, port) the original flow's destination is rewritten to; it is registered in the
/// loop-prevention registry so reverse packets on this leg are not re-evaluated as a new client flow.
/// </summary>
public interface ITcpRedirectListener : IAsyncDisposable
{
    Endpoint TranslatedTuple { get; }
    ValueTask<ITcpAcceptedConnection> AcceptAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A connection accepted on the redirect listener. The remote endpoint identifies which translated
/// flow the connection belongs to, letting the coordinator resolve the original flow and its saved
/// destination. Kept minimal in 8b; the real relay pump belongs to 8c.
/// </summary>
public interface ITcpAcceptedConnection : IAsyncDisposable
{
    Endpoint RemoteEndPoint { get; }
}

/// <summary>
/// Establishes the upstream SOCKS5 relay for an accepted redirected connection. The concrete
/// implementation (8c) opens a SOCKS5 control socket, performs CONNECT for the original destination,
/// and pumps bytes between the accepted local socket and the upstream socket with bounded buffers
/// and backpressure. The fake (8b tests) asserts it received the correct original destination.
/// </summary>
public interface ITcpProxyRelayFactory
{
    ValueTask<ITcpRelay> EstablishAsync(Endpoint originalDestination, ITcpAcceptedConnection acceptedConnection, CancellationToken cancellationToken);
}

/// <summary>
/// A relay between an accepted redirect-leg connection and the upstream SOCKS5 socket. <see cref="Completion"/>
/// transitions to a terminal state when the relay ends (either peer half-closed, errored, or torn down).
/// The real byte pump is 8c; 8b uses a fake whose Completion is controlled by the test.
/// </summary>
public interface ITcpRelay : IAsyncDisposable
{
    Task Completion { get; }
}

/// <summary>
/// Injects a rewritten TCP frame back into the packet path. For a SYN redirect the frame is injected
/// toward the original capture direction so the Windows stack delivers it to the local listener; for
/// a reverse packet it is injected toward the origin host stack. The concrete implementation (8c)
/// drives the NDISAPI driver; a fake (8b tests) records the frame for assertions.
/// </summary>
public interface ITcpRedirectInjector
{
    ValueTask InjectAsync(ReadOnlyMemory<byte> rewrittenFrame, bool isOnSend, nint adapterHandle, CancellationToken cancellationToken);
}

/// <summary>
/// The terminal outcome of a proxy-selected TCP packet. A proxy-selected flow is never silently
/// passed: any setup, rewrite, or injection failure fails closed as <see cref="Blocked"/>.
/// </summary>
public enum TcpRedirectOutcome
{
    Injected,
    Blocked,
}
