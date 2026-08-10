using System.Runtime.Versioning;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;

namespace WinForward.Runtime;

/// <summary>
/// A reinjection target for a UDP response: the NDISAPI enumeration handle of the adapter the
/// response is injected toward and the MAC to use when rebuilding the Ethernet header. Keyed by
/// adapter stable ID in <see cref="UdpResponseReinjector"/> so forwarded (Hyper-V/VM) flows can
/// route responses toward their origin adapter instead of always the host adapter.
/// </summary>
public readonly record struct UdpAdapterTarget(nint Handle, byte[] Mac);

/// <summary>
/// Reinjects SOCKS5 UDP relay responses back toward the original client. A response datagram
/// arrives from the relay with the real server endpoint as its source; the sink rebuilds a complete
/// Ethernet II + IPv4/IPv6 + UDP frame with that server as source and the original flow's local
/// endpoint as destination, then injects it toward MSTCP (host flow) or back to the origin adapter
/// (forwarded flow), mirroring the TCP forwarded-direction fix. Frame-build failures, an unresolved
/// forwarded origin adapter, or a payload over the pinned frame cap drop the response (fail-closed)
/// without throwing into the coordinator's receive loop.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UdpResponseReinjector : IUdpResponseSink
{
    private static readonly TimeSpan MissingOriginLogInterval = TimeSpan.FromSeconds(5);

    private readonly IPacketReinjector _reinjector;
    private readonly UdpAdapterTarget _host;
    private readonly IReadOnlyDictionary<string, UdpAdapterTarget> _byStableId;
    private readonly int _maximumFrameSize;
    private readonly IRuntimeLogger _logger;
    private long _lastMissingOriginLogTicks;

    /// <summary>
    /// Creates a response reinjector for a capture scope whose host-side adapter is identified by
    /// <paramref name="hostHandle"/> (the NDISAPI enumeration handle) and whose MAC is
    /// <paramref name="hostMac"/>. <paramref name="adaptersByStableId"/> maps adapter stable IDs
    /// (arbitrary injectable seam, fake-constructed in tests) to their reinjection targets so a
    /// forwarded flow's response can be sent toward its origin adapter; the host entry is not
    /// required in the map. <paramref name="maximumFrameSize"/> is the pinned NDISAPI frame cap
    /// (default 1514, or 9014 for a jumbo-capable ABI) that bounds rebuilt frames (M3).
    /// </summary>
    public UdpResponseReinjector(
        IPacketReinjector reinjector,
        nint hostHandle,
        ReadOnlySpan<byte> hostMac,
        IReadOnlyDictionary<string, UdpAdapterTarget>? adaptersByStableId = null,
        int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame,
        IRuntimeLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(reinjector);
        if (hostMac.Length != 6) throw new ArgumentOutOfRangeException(nameof(hostMac), "The host adapter MAC must be exactly 6 bytes.");
        if (maximumFrameSize <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFrameSize));
        _reinjector = reinjector;
        _host = new UdpAdapterTarget(hostHandle, hostMac.ToArray());
        _byStableId = adaptersByStableId ?? new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase);
        _maximumFrameSize = maximumFrameSize;
        _logger = logger ?? NullRuntimeLogger.Instance;
    }

    public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        // Host flows reinject toward MSTCP on the host adapter; forwarded flows reinject toward the
        // origin adapter (the adapter the flow was first observed ON_RECEIVE on), using that
        // adapter's handle and MAC (H2). If a forwarded flow's origin adapter cannot be resolved,
        // the response is dropped fail-closed with a rate-limited log rather than sent out the
        // wrong (host) adapter where the VM could never receive it.
        var target = _host;
        var towardMstcp = true;
        if (originalFlow.Origin == FlowOriginKind.Forwarded)
        {
            if (originalFlow.OriginAdapterId is null || !_byStableId.TryGetValue(originalFlow.OriginAdapterId, out var originAdapter))
            {
                LogMissingOriginAdapter();
                return ValueTask.CompletedTask;
            }
            target = originAdapter;
            towardMstcp = false;
        }

        if (!UdpFrameBuilder.TryBuild(
                remoteSource.Address,
                remoteSource.Port,
                originalFlow.Local.Address,
                originalFlow.Local.Port,
                payload,
                target.Mac,
                target.Mac,
                out var frame,
                _maximumFrameSize))
        {
            _logger.Warn("UDP response frame build failed; dropping the response (fail-closed).");
            return ValueTask.CompletedTask;
        }

        using var buffer = new NdisPacketBuffer();
        // Per the WinpkFilter pass/revert matrix (design §1): toward MSTCP the frame simulates a
        // receive (ON_RECEIVE); toward an adapter it is an ON_SEND. The forwarded (Hyper-V)
        // direction previously reused the ON_RECEIVE flag and could not reach the VM (H3).
        buffer.SetFrame(frame, towardMstcp ? NdisApiAbi.PacketFlagOnReceive : NdisApiAbi.PacketFlagOnSend, target.Handle);
        if (towardMstcp)
        {
            _reinjector.SendToMstcp(target.Handle, buffer);
        }
        else
        {
            _reinjector.SendToAdapter(target.Handle, buffer);
        }
        return ValueTask.CompletedTask;
    }

    private void LogMissingOriginAdapter()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastMissingOriginLogTicks);
        if (now - last >= MissingOriginLogInterval.Ticks && Interlocked.CompareExchange(ref _lastMissingOriginLogTicks, now, last) == last)
        {
            _logger.Warn("Forwarded UDP response dropped fail-closed: the flow's origin adapter is not resolved in the reinjection map.");
        }
    }
}