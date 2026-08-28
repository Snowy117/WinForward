using System.Net.Sockets;

namespace WinForward.Core.Tests;

/// <summary>
/// A socket double that tracks disposal through the protected <see cref="Socket.Dispose(bool)"/>
/// path, with optional hooks for a pre-dispose callback and a synthetic (Dispose-path-only)
/// disposal failure. Defaults to a UDP datagram socket; TCP control connections pass explicit
/// socket/protocol types.
/// </summary>
internal sealed class TrackingSocket : Socket
{
    public TrackingSocket(AddressFamily addressFamily, SocketType socketType = SocketType.Dgram, ProtocolType protocolType = ProtocolType.Udp) : base(addressFamily, socketType, protocolType) { }
    public bool IsDisposedValue { get; private set; }
    public Action? OnDisposing { get; set; }
    public Exception? DisposeException { get; set; }

    protected override void Dispose(bool disposing)
    {
        OnDisposing?.Invoke();
        IsDisposedValue = true;
        base.Dispose(disposing);
        // A finalizer must never throw: the synthetic disposal failure is reserved for the
        // controlled Dispose() path, otherwise an unreleased test double crashes the host.
        if (disposing && DisposeException is not null) throw DisposeException;
    }
}
