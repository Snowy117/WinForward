using System.Runtime.Versioning;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;

namespace WinForward.Runtime;

/// <summary>
/// Reinjects SOCKS5 UDP relay responses back toward the original client. A response datagram
/// arrives from the relay with the real server endpoint as its source; the sink rebuilds a complete
/// Ethernet II + IPv4/IPv6 + UDP frame with that server as source and the original flow's local
/// endpoint as destination, then injects it toward MSTCP (host flow) or back to the origin adapter
/// (forwarded flow), mirroring the TCP forwarded-direction fix. Frame-build failures drop the
/// response (fail-closed) without throwing into the coordinator's receive loop.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UdpResponseReinjector : IUdpResponseSink
{
    private readonly IPacketReinjector _reinjector;
    private readonly nint _adapterHandle;
    private readonly byte[] _localMac;
    private readonly IRuntimeLogger _logger;

    /// <summary>
    /// Creates a response reinjector for a capture scope whose host-side adapter is identified by
    /// <paramref name="adapterHandle"/> (the NDISAPI enumeration handle) and whose MAC is
    /// <paramref name="localMac"/>. The handle is required because the response sink carries no
    /// captured packet metadata; the MAC is used as both the source and destination MAC of every
    /// rebuilt frame because the capture is on the client's own adapter for host flows; forwarded
    /// (VM) MAC handling is a later milestone.
    /// </summary>
    public UdpResponseReinjector(IPacketReinjector reinjector, nint adapterHandle, ReadOnlySpan<byte> localMac, IRuntimeLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(reinjector);
        if (localMac.Length != 6) throw new ArgumentOutOfRangeException(nameof(localMac), "The host adapter MAC must be exactly 6 bytes.");
        _reinjector = reinjector;
        _adapterHandle = adapterHandle;
        _localMac = localMac.ToArray();
        _logger = logger ?? NullRuntimeLogger.Instance;
    }

    public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (!UdpFrameBuilder.TryBuild(
                remoteSource.Address,
                remoteSource.Port,
                originalFlow.Local.Address,
                originalFlow.Local.Port,
                payload,
                _localMac,
                _localMac,
                out var frame))
        {
            _logger.Warn("UDP response frame build failed; dropping the response (fail-closed).");
            return ValueTask.CompletedTask;
        }

        using var buffer = new NdisPacketBuffer();
        // SendToMstcp simulates a receive from the selected interface upward into the Windows
        // TCP/IP stack; SendToAdapter injects toward the interface. A response frame is always
        // tagged ON_RECEIVE regardless of the original capture direction.
        buffer.SetFrame(frame, NdisApiAbi.PacketFlagOnReceive, _adapterHandle);
        if (originalFlow.Origin == FlowOriginKind.Host)
        {
            _reinjector.SendToMstcp(_adapterHandle, buffer);
        }
        else
        {
            _reinjector.SendToAdapter(_adapterHandle, buffer);
        }
        return ValueTask.CompletedTask;
    }
}