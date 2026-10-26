using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WinForward.Configuration;
using WinForward.Runtime.TcpRedirect;
using WinForward.Userland.Device;
using WinForward.Userland.Relay;

namespace WinForward.Cli.Backends.Kernel;

/// <summary>
/// One TCP connection the Kernel driver redirected to the local listener: read the redirect context,
/// resolve the rule's target, take a capacity slot, establish the relay, and observe how it ends.
/// </summary>
/// <remarks>
/// The session owns the accepted socket from construction. The relay takes it over on a successful
/// establishment — its pumps close it, abortively for a non-clean end — and every other path closes it
/// here, abortively, so a client whose connection was refused sees a reset instead of a connection that
/// is never served.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class KernelRelaySession(Socket socket, KernelHostTcpService host, ILogger logger)
{
    private static readonly HostRedirectContextSource s_contextSource = new();

    /// <summary>The relay for this connection, once established; it owns the client socket from then on.</summary>
    private ITcpRelay? _relay;

    /// <summary>Why the relay ended; null while the connection has no relay or the shutdown ended it.</summary>
    private RelayEndKind? _endKind;

    internal async Task RunAsync(CancellationToken token)
    {
        try
        {
            await ServeAsync(token).ConfigureAwait(false);
        }
        finally
        {
            // One owner per accepted socket: an established relay's disposal closes the socket itself —
            // a non-clean end was already closed abortively from inside its pumps — and the session
            // closes it on every path that never reached a relay.
            if (_relay is { } relay) await relay.DisposeAsync().ConfigureAwait(false);

            CloseAcceptedConnection();
        }
    }

    /// <summary>
    /// Closes the accepted connection without a relay, abortively. A caller that refused to start the
    /// session at all reaches this too, because the session owns the socket from construction.
    /// </summary>
    internal void CloseAcceptedConnection()
    {
        if (_relay is not null) return;
        SetAbortiveLinger(socket);
        socket.Dispose();
    }

    private async Task ServeAsync(CancellationToken token)
    {
        var peer = socket.RemoteEndPoint;
        if (!TryResolve(peer, out var destination, out var target)) return;

        KernelBackendLog.SessionAccepted(logger, peer, destination.EndPoint, destination.RuleIndex, destination.ProcessId);
        if (!host.TryAdmitSession())
        {
            host.CountCapacityBlocked();
            KernelBackendLog.CapacityBlocked(logger, host.FlowCapacity, peer, destination.EndPoint);
            return;
        }

        try
        {
            await RelayAsync(destination, target, token).ConfigureAwait(false);
        }
        finally
        {
            host.ReleaseSession();
        }
    }

    /// <summary>
    /// Reads the original destination and resolves the rule's target. Each refusal is counted and
    /// logged here, and none of them is a fallback: an unresolved index means the blob and the target
    /// table disagree, which must never be papered over with a default target.
    /// </summary>
    private bool TryResolve(EndPoint? peer, [NotNullWhen(true)] out RedirectDestination? destination, out ProxyTarget target)
    {
        destination = null;
        target = default;
        if (!s_contextSource.TryRead(socket, out var parsed, out var error))
        {
            host.CountRefused();
            KernelBackendLog.ContextFailed(logger, error, peer);
            return false;
        }

        // The driver redirects host UDP associations to the same listener port; a UDP context must
        // never be relayed as a TCP connection.
        if (parsed.Protocol != RedirectContext.ProtocolTcp)
        {
            host.CountRefused();
            var reason = $"the redirect context protocol is {parsed.ProtocolText}, not TCP";
            KernelBackendLog.SessionRefused(logger, reason, peer);
            return false;
        }

        if (!host.TryResolveTarget(parsed.RuleIndex, out target, out var mappingError))
        {
            host.CountRefused();
            KernelBackendLog.SessionRefused(logger, mappingError, peer);
            return false;
        }

        destination = parsed;
        return true;
    }

    private async Task RelayAsync(RedirectDestination destination, ProxyTarget target, CancellationToken token)
    {
        _relay = await EstablishAsync(destination, target, token).ConfigureAwait(false);
        if (_relay is null) return;

        host.CountRelayEstablished();
        KernelBackendLog.RelayEstablished(logger, destination.EndPoint, target.Name, destination.RuleIndex, destination.ProcessId);

        _endKind = await AwaitEndAsync(_relay, destination, token).ConfigureAwait(false);
        ReportEnd(destination, target);
    }

    /// <summary>
    /// Establishes the relay behind the setup gate. A refusal (shutdown) or a failure leaves the socket
    /// with this session and returns null; the caller has already been logged and counted either way.
    /// </summary>
    private async Task<ITcpRelay?> EstablishAsync(RedirectDestination destination, ProxyTarget target, CancellationToken token)
    {
        var slotHeld = false;
        try
        {
            await host.EnterSetupSlotAsync(token).ConfigureAwait(false);
            slotHeld = true;
            return await host.EstablishRelayAsync(destination, socket, target.Socks5!, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            KernelBackendLog.SessionRefused(logger, "shutdown while the relay setup waited for a slot", socket.RemoteEndPoint);
            return null;
        }
        catch (Exception exception)
        {
            host.CountRelaySetupFailed();
            KernelBackendLog.RelaySetupFailed(logger, destination.EndPoint, target.Name, exception.GetType().Name, exception.Message);
            return null;
        }
        finally
        {
            if (slotHeld) host.ExitSetupSlot();
        }
    }

    /// <summary>
    /// Waits for the relay to end on its own, or for the shutdown to request it. A null result means
    /// the shutdown won: the relay's disposal below is what terminates it then, so a session parked on
    /// the relay's own stall window can never hold the drain open.
    /// </summary>
    private async Task<RelayEndKind?> AwaitEndAsync(ITcpRelay relay, RedirectDestination destination, CancellationToken token)
    {
        try
        {
            await relay.Completion.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            // The relay reports its own pump fault on Completion and observes it again when it is
            // disposed, so this classification is the end-kind decision, not the fault's only observer.
            var error = exception.GetType().Name;
            KernelBackendLog.RelayFaulted(logger, destination.EndPoint, error);
            return RelayEndKind.Faulted;
        }

        return relay is ITcpRelayEndInfo info ? info.EndKind : RelayEndKind.CleanEnded;
    }

    private void ReportEnd(RedirectDestination destination, ProxyTarget target)
    {
        if (_endKind is not { } endKind)
        {
            KernelBackendLog.RelayAbandoned(logger, destination.EndPoint);
            return;
        }

        host.CountRelayEnd(endKind);
        var serverBytes = _relay is ITcpRelayEndInfo info ? info.ServerStreamBytes : 0;
        KernelBackendLog.RelayEnded(logger, destination.EndPoint, target.Name, endKind, serverBytes);
    }

    private static void SetAbortiveLinger(Socket target)
    {
        try
        {
            target.LingerState = new LingerOption(enable: true, seconds: 0);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or SocketException)
        {
            // The socket is already gone, and the abortive intent needs no retry.
        }
    }
}
