using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime.Capture;

namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// A reinjection target for a UDP response: the NDISAPI enumeration handle of the adapter the
/// response is injected toward and the MAC to use when rebuilding the Ethernet header. Keyed by
/// adapter stable ID in <see cref="UdpResponseReinjector"/> so every flow can route responses
/// toward the adapter on which it was captured instead of always using one startup-selected adapter.
/// </summary>
public readonly record struct UdpAdapterTarget(nint Handle, byte[] Mac);

/// <summary>
/// The seam a UDP proxy session calls to deliver a relay response back toward the original
/// client. The coordinator owns session lifetimes; the sink only rebuilds and injects frames.
/// </summary>
public interface IUdpResponseSink
{
    ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, byte[]? clientMac, CancellationToken cancellationToken);
}

/// <summary>
/// Reinjects SOCKS5 UDP relay responses back toward the original client. A response datagram
/// arrives from the relay with the real server endpoint as its source; the sink rebuilds a complete
/// Ethernet II + IPv4/IPv6 + UDP frame with that server as source and the original flow's local
/// endpoint as destination, then injects it toward MSTCP (host flow) or back to the origin adapter
/// (forwarded flow), mirroring the TCP forwarded-direction fix. Frame-build failures, an unresolved
/// forwarded origin adapter, or a payload over the pinned frame cap drop the response (fail-closed)
/// without throwing into the coordinator's receive loop. A host flow whose origin adapter has
/// disappeared falls back to the startup-selected host adapter with a rate-limited warning.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UdpResponseReinjector : IUdpResponseSink
{
    private static readonly TimeSpan MissingOriginLogInterval = TimeSpan.FromSeconds(5);

    private readonly IPacketReinjector _reinjector;
    private readonly UdpAdapterTarget _host;
    private readonly IReadOnlyDictionary<string, UdpAdapterTarget> _byStableId;
    private readonly NdisPacketBufferPool _bufferPool;
    private readonly int _maximumFrameSize;
    private readonly IRuntimeLogger _logger;
    private long _lastMissingOriginLogTicks;
    private long _lastHostFallbackLogTicks;
    private long _lastMissingClientMacLogTicks;
    private long _lastFrameBuildFailureLogTicks;

    /// <summary>
    /// Creates a response reinjector for a capture scope whose host-side adapter is identified by
    /// <paramref name="hostHandle"/> (the NDISAPI enumeration handle) and whose MAC is
    /// <paramref name="hostMac"/>. <paramref name="adaptersByStableId"/> maps adapter stable IDs
    /// (arbitrary injectable seam, fake-constructed in tests) to their reinjection targets so a
    /// flow's response can be sent toward its origin adapter; the startup-selected host entry is
    /// not required in the map. <paramref name="maximumFrameSize"/> is the pinned NDISAPI frame cap
    /// (default 1514, or 9014 for a jumbo-capable ABI) that bounds rebuilt frames (M3).
    /// <paramref name="bufferPool"/> supplies the native buffers responses are built into
    /// (pooled reuse instead of a per-response allocation).
    /// </summary>
    public UdpResponseReinjector(
        IPacketReinjector reinjector,
        nint hostHandle,
        ReadOnlySpan<byte> hostMac,
        IReadOnlyDictionary<string, UdpAdapterTarget>? adaptersByStableId = null,
        int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame,
        IRuntimeLogger? logger = null,
        NdisPacketBufferPool? bufferPool = null)
    {
        ArgumentNullException.ThrowIfNull(reinjector);
        if (hostMac.Length != 6) throw new ArgumentOutOfRangeException(nameof(hostMac), "The host adapter MAC must be exactly 6 bytes.");
        if (maximumFrameSize <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFrameSize));
        _reinjector = reinjector;
        _host = new UdpAdapterTarget(hostHandle, hostMac.ToArray());
        _byStableId = adaptersByStableId ?? new Dictionary<string, UdpAdapterTarget>(StringComparer.OrdinalIgnoreCase);
        _maximumFrameSize = maximumFrameSize;
        _logger = logger ?? NullRuntimeLogger.Instance;
        _bufferPool = bufferPool ?? NdisPacketBufferPool.Shared;
    }

    public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, byte[]? clientMac, CancellationToken cancellationToken)
    {
        if (!TryResolveTarget(originalFlow, clientMac, out var target, out var towardMstcp, out var destinationMac))
        {
            return ValueTask.CompletedTask;
        }

        var buffer = _bufferPool.Rent();
        try
        {
            // The frame is built in place into the pooled native buffer (R4): no managed byte[]
            // per response on the steady path.
            if (!UdpFrameBuilder.TryBuildInto(
                    remoteSource.Address,
                    remoteSource.Port,
                    originalFlow.Local.Address,
                    originalFlow.Local.Port,
                    payload,
                    target.Mac,
                    destinationMac,
                    buffer.GetFrameStorage(),
                    out var frameLength,
                    _maximumFrameSize))
            {
                if (_logger.IsEnabled(RuntimeLogLevel.Trace))
                {
                    LogTrace("udp.response.dropped", originalFlow,
                        new RuntimeLogField("reason", "frameBuild"), new RuntimeLogField("bytes", payload.Length));
                }
                LogFrameBuildFailure();
                return ValueTask.CompletedTask;
            }

            // Per the WinpkFilter pass/revert matrix (design §1): toward MSTCP the frame simulates a
            // receive (ON_RECEIVE); toward an adapter it is an ON_SEND. The forwarded (Hyper-V)
            // direction previously reused the ON_RECEIVE flag and could not reach the VM (H3).
            buffer.CompleteFrame(frameLength, towardMstcp ? NdisApiAbi.PacketFlagOnReceive : NdisApiAbi.PacketFlagOnSend, target.Handle);
            if (towardMstcp)
            {
                _reinjector.SendToMstcp(target.Handle, buffer);
            }
            else
            {
                _reinjector.SendToAdapter(target.Handle, buffer);
            }
            if (_logger.IsEnabled(RuntimeLogLevel.Trace))
            {
                LogTrace("udp.response.reinjected", originalFlow,
                    new RuntimeLogField("target", towardMstcp ? "mstcp" : "adapter"),
                    new RuntimeLogField("bytes", payload.Length));
            }
            return ValueTask.CompletedTask;
        }
        finally
        {
            // Dispose of a pooled buffer returns it to the pool exactly once.
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Resolves the reinjection target and the Ethernet destination MAC for a response. Host flows
    /// reinject toward MSTCP on their capture adapter with that adapter's MAC on both header slots;
    /// a missing capture adapter falls back to the startup-selected host adapter. Forwarded flows
    /// reinject toward the origin adapter (H2) with the origin adapter's MAC as source and the
    /// recorded client MAC as destination so the vSwitch delivers to the VM instead of the host
    /// stack. A forwarded flow with an unresolved origin adapter or without a recorded client MAC
    /// is dropped fail-closed with a rate-limited log rather than sent out the wrong adapter.
    /// </summary>
    private bool TryResolveTarget(FlowKey originalFlow, byte[]? clientMac, out UdpAdapterTarget target, out bool towardMstcp, out byte[]? destinationMac)
    {
        target = _host;
        towardMstcp = true;
        if (originalFlow.Origin != FlowOriginKind.Forwarded)
        {
            if (originalFlow.OriginAdapterId is { } originAdapterId && _byStableId.TryGetValue(originAdapterId, out var hostOriginAdapter))
            {
                target = hostOriginAdapter;
            }
            else
            {
                LogHostAdapterFallback();
            }
            destinationMac = target.Mac;
            return true;
        }

        if (originalFlow.OriginAdapterId is null || !_byStableId.TryGetValue(originalFlow.OriginAdapterId, out var originAdapter))
        {
            destinationMac = null;
            if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogTrace("udp.response.dropped", originalFlow, new RuntimeLogField("reason", "missingOriginAdapter"));
            LogMissingOriginAdapter();
            return false;
        }
        target = originAdapter;
        towardMstcp = false;
        if (clientMac is null || clientMac.Length != 6)
        {
            destinationMac = null;
            if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogTrace("udp.response.dropped", originalFlow, new RuntimeLogField("reason", "missingClientMac"));
            LogMissingClientMac();
            return false;
        }
        destinationMac = clientMac;
        return true;
    }

    private void LogTrace(string eventName, FlowKey flow, params RuntimeLogField[] fields)
    {
        if (!_logger.IsEnabled(RuntimeLogLevel.Trace)) return;
        var allFields = new RuntimeLogField[fields.Length + 4];
        allFields[0] = new("protocol", flow.Protocol);
        allFields[1] = new("origin", flow.Origin);
        allFields[2] = new("source", flow.Local);
        allFields[3] = new("destination", flow.Remote);
        fields.CopyTo(allFields, 4);
        _logger.Event(RuntimeLogLevel.Trace, eventName, allFields);
    }

    private void LogFrameBuildFailure()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastFrameBuildFailureLogTicks);
        if (now - last >= MissingOriginLogInterval.Ticks && Interlocked.CompareExchange(ref _lastFrameBuildFailureLogTicks, now, last) == last)
        {
            _logger.Warn("UDP response frame build failed; dropping the response (fail-closed).");
        }
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

    private void LogMissingClientMac()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastMissingClientMacLogTicks);
        if (now - last >= MissingOriginLogInterval.Ticks && Interlocked.CompareExchange(ref _lastMissingClientMacLogTicks, now, last) == last)
        {
            _logger.Warn("Forwarded UDP response dropped fail-closed: the flow's client MAC was not recorded.");
        }
    }

    private void LogHostAdapterFallback()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastHostFallbackLogTicks);
        if (now - last >= MissingOriginLogInterval.Ticks && Interlocked.CompareExchange(ref _lastHostFallbackLogTicks, now, last) == last)
        {
            _logger.Warn("Host UDP response origin adapter is not resolved in the reinjection map; using the startup fallback adapter.");
        }
    }
}
