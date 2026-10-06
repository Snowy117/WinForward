using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinForward.Configuration;
using WinForward.Core;

namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// The per-datagram anomaly that made a transport receive undeliverable. Skip reasons are surfaced
/// as results instead of exceptions so a single bad relay datagram never terminates a session's
/// receive loop; only socket-level failures keep throwing.
/// </summary>
public enum UdpTransportSkipReason
{
    /// <summary>Not a skip: the datagram was decoded successfully.</summary>
    None = 0,

    /// <summary>The datagram arrived from an endpoint other than the one this transport accepts replies from (port or address-family mismatch).</summary>
    UnexpectedSource = 1,

    /// <summary>The datagram filled the receive buffer completely and may be truncated.</summary>
    Oversized = 2,

    /// <summary>The datagram is not decodable by this transport (SOCKS5: it is not a valid SOCKS5 UDP datagram).</summary>
    Malformed = 3,

    /// <summary>
    /// The receive call itself faulted with <see cref="SocketError.ConnectionReset"/>: on Windows an
    /// ICMP port-unreachable answering one of this socket's sends surfaces this way. Skip-class like
    /// the datagram anomalies: the session keeps receiving (S2).
    /// </summary>
    ConnectionReset = 4,
}

/// <summary>
/// One decoded transport datagram handed to the session's receive loop: the reply's declared source
/// (an address or a domain, plus the port) and its payload. <see cref="SourceAddress"/> is the raw
/// value-type representation (<see cref="IPAddressValue"/>) so decoding materializes no
/// framework address; a null address marks a domain-typed datagram, which the session cannot rebuild
/// an IP frame from. A null address with a null <see cref="SourceDomain"/> is the "no decodable
/// source" shape and reaches the same skip class.
/// </summary>
public readonly record struct UdpTransportDatagram(IPAddressValue? SourceAddress, string? SourceDomain, ushort SourcePort, ReadOnlyMemory<byte> Payload);

/// <summary>
/// The discriminated result of <see cref="IUdpProxyTransport.ReceiveAsync"/>: either a decoded
/// datagram (<see cref="HasDatagram"/>) or a <see cref="SkipReason"/> that skips exactly one
/// datagram. A struct result keeps the receive hot path allocation-free.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct UdpTransportReceiveResult(UdpTransportDatagram Datagram, UdpTransportSkipReason SkipReason)
{
    /// <summary>True when <see cref="Datagram"/> carries a decoded relay datagram.</summary>
    public bool HasDatagram => SkipReason == UdpTransportSkipReason.None;

    /// <summary>Wraps a successfully decoded relay datagram.</summary>
    internal static UdpTransportReceiveResult Received(UdpTransportDatagram datagram) => new(datagram, UdpTransportSkipReason.None);

    /// <summary>Marks one per-datagram anomaly; the caller must skip the datagram and keep receiving.</summary>
    internal static UdpTransportReceiveResult Skipped(UdpTransportSkipReason reason) => new(default, reason);
}

/// <summary>
/// The flow's authenticated UDP association is gone: its control connection ended and the watchdog
/// recorded the death. The transport refuses further datagrams with this exception before touching
/// its socket, and the coordinator removes the flow's slot with
/// <c>UdpTeardownReason.AssociationLost</c> without arming the setup cooldown, so the flow
/// re-establishes on its next datagram.
/// </summary>
#pragma warning disable RCS1194 // The [SerializationInfo, StreamingContext] constructor is deliberately omitted: binary serialization is obsolete in .NET 8+ (SYSLIB0051) and this exception carries no state beyond its message and inner exception.
public sealed class UdpAssociationLostException : IOException
{
    // ReSharper disable once UnusedMember.Global // Conventional exception surface: RCS1194 requires the parameterless and message-only constructors, even though in-tree callers use only the (message, inner) overload.
    public UdpAssociationLostException() { }

    // ReSharper disable once UnusedMember.Global // Conventional exception surface: RCS1194 requires the parameterless and message-only constructors, even though in-tree callers use only the (message, inner) overload.
    public UdpAssociationLostException(string message) : base(message) { }

    public UdpAssociationLostException(string message, Exception? innerException) : base(message, innerException) { }
}
#pragma warning restore RCS1194

/// <summary>
/// The relay handshake was rejected and the rejection was discovered after the transport's setup
/// call returned: a transport whose handshake completes lazily on the receive path cannot observe a
/// refused setup reply from the call that created it, so the refusal surfaces here instead. The
/// coordinator removes the flow's slot with <c>UdpTeardownReason.SetupFailure</c>, arming the setup
/// cooldown, exactly as a refused <c>UDP ASSOCIATE</c> does on the native path — so a server that
/// systematically rejects the exchange is re-dialed once per cooldown instead of once per client
/// retransmit.
/// </summary>
#pragma warning disable RCS1194 // The [SerializationInfo, StreamingContext] constructor is deliberately omitted: binary serialization is obsolete in .NET 8+ (SYSLIB0051) and this exception carries no state beyond its message and inner exception.
public sealed class UdpTransportHandshakeRejectedException : IOException
{
    // ReSharper disable once UnusedMember.Global // Conventional exception surface: RCS1194 requires the parameterless and message-only constructors, even though in-tree callers use only the (message, inner) overload.
    public UdpTransportHandshakeRejectedException() { }

    // ReSharper disable once UnusedMember.Global // Conventional exception surface: RCS1194 requires the parameterless and message-only constructors, even though in-tree callers use only the (message, inner) overload.
    public UdpTransportHandshakeRejectedException(string message) : base(message) { }

    public UdpTransportHandshakeRejectedException(string message, Exception? innerException) : base(message, innerException) { }
}
#pragma warning restore RCS1194

/// <summary>
/// The per-flow UDP transport seam: one instance per flow, owned by <c>UdpProxySession</c>, which
/// sends the flow's datagrams and reports replies as
/// <see cref="UdpTransportReceiveResult"/>. The contract is transport-neutral and knows nothing
/// about what a payload carries — the SOCKS5 implementation is the one that encodes a destination
/// into its wire header — so a second, non-SOCKS5 implementation can sit behind the same factory
/// without touching the session, the coordinator, or the reinjector.
/// </summary>
public interface IUdpProxyTransport : IAsyncDisposable
{
    /// <summary>
    /// The endpoint replies are expected from and the transport's own traffic is keyed on: the SOCKS5
    /// relay for a relayed flow, the configured local endpoint for a local target. Fixed for the
    /// transport's life: a relayed flow owns the association that negotiated it.
    /// </summary>
    IPEndPoint PeerEndpoint { get; }

    /// <summary>This flow's own local transport endpoint: the half of the flow's identity pair that is local to this host (a relay alias for a relayed flow, the flow's own socket for a local target).</summary>
    IPEndPoint LocalEndpoint { get; }

    /// <summary>
    /// Sends one datagram to <paramref name="destination"/>. The payload is consumed synchronously
    /// (copied or encoded into a reusable send buffer, then a non-blocking kernel send) before any
    /// asynchronous socket operation, so a native capture buffer whose span backs it is free to
    /// recycle once this call returns. Only the contended-gate slow shape cannot keep the span
    /// across its await and copies it (cold path).
    /// </summary>
    ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken);

    /// <summary>Receives one datagram, or the skip that stands for the one anomaly that made this receive undeliverable.</summary>
    ValueTask<UdpTransportReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>
/// Creates the per-flow transport for a resolved <see cref="ProxyTarget"/>. Each implementation
/// serves targets of the kind it implements and refuses any other up front, so a mis-composed
/// factory fails closed instead of dereferencing a target it does not understand.
/// </summary>
public interface IUdpProxyTransportFactory
{
    ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken);
}

/// <summary>
/// Dispatches a resolved target to the transport kind that serves it: a local target to the local
/// transport, a SOCKS5 server to the relay transport. A target that carries neither kind is refused
/// before any factory runs, so an unshaped <see cref="ProxyTarget"/> can never reach a transport that
/// would misinterpret it. Adding a transport kind means one more branch here; the coordinator, the
/// session, and the reinjector stay untouched.
/// </summary>
#pragma warning disable MA0182 // Consumed by the composition root (WinForward.Cli) through InternalsVisibleTo; the analyzer only sees usages inside this assembly.
internal sealed class UdpTransportFactory(IUdpProxyTransportFactory socks5, IUdpProxyTransportFactory local) : IUdpProxyTransportFactory
{
    public ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken) => target switch
    {
        { Local: not null } => local.CreateAsync(target, cancellationToken),
        { Socks5: not null } => socks5.CreateAsync(target, cancellationToken),
        _ => throw new InvalidOperationException($"The resolved target '{target.Name}' carries neither a SOCKS5 server nor a local endpoint."),
    };
}
#pragma warning restore MA0182

/// <summary>
/// The receive-path fault vocabulary shared by the transports: a <see cref="SocketError.ConnectionReset"/>
/// is skip-class (S2: an ICMP port-unreachable answering one of this socket's sends must not end a
/// session's receive loop), while every other socket fault is fatal and keeps tearing the session down.
/// </summary>
internal static class UdpTransportReceiveClassifier
{
    /// <summary>Maps a receive-path fault to its skip reason, or null when the fault is fatal.</summary>
    internal static UdpTransportSkipReason? ClassifyFault(Exception exception) =>
        exception is SocketException { SocketErrorCode: SocketError.ConnectionReset }
            ? UdpTransportSkipReason.ConnectionReset
            : null;
}

/// <summary>
/// The per-flow exchange evidence a retention policy reads: the two counters the concrete
/// transport maintains itself (the SOCKS5 transport counts its own accepted sends and its first
/// decoded relay response), exposed on the transport seam because that accounting is private to the
/// implementation. Both reads are plain volatile loads off the packet path, and a transport that
/// does not implement this interface is classified as <em>sustained</em> — the retention-safe
/// direction, and what keeps a foreign or fake transport's sweep behaviour unchanged.
/// </summary>
internal interface IUdpExchangeCounters
{
    /// <summary>The datagrams this flow sent successfully.</summary>
    int DatagramsSent { get; }

    /// <summary>Whether this flow ever decoded a relay response.</summary>
    bool SawResponse { get; }
}
