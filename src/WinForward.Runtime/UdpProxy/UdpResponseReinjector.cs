using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime.Capture;

namespace WinForward.Runtime.UdpProxy;

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
/// (forwarded flow), mirroring the TCP forwarded-direction fix. Reinjection targets are resolved
/// per response through <see cref="IUdpAdapterTargetSource"/> so a capture refresh that
/// re-enumerates adapter handles is picked up without reconstructing the sink. Frame-build
/// failures, an unresolved forwarded origin adapter, or a payload over the pinned frame cap drop
/// the response (fail-closed) without throwing into the coordinator's receive loop. A host flow
/// whose origin adapter has disappeared falls back to the source's host adapter with a
/// rate-limited warning; when no host target exists either, the response drops fail-closed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UdpResponseReinjector : IUdpResponseSink
{
    private static readonly TimeSpan MissingOriginLogInterval = TimeSpan.FromSeconds(5);

    private readonly IPacketReinjector _reinjector;
    private readonly IUdpAdapterTargetSource _adapterTargets;
    private readonly NdisPacketBufferPool _bufferPool;
    private readonly int _maximumFrameSize;
    private readonly IRuntimeLogger _logger;
    private long _lastMissingOriginLogTicks;
    private long _lastHostFallbackLogTicks;
    private long _lastMissingHostTargetLogTicks;
    private long _lastMissingClientMacLogTicks;
    private long _lastFrameBuildFailureLogTicks;

    /// <summary>
    /// Creates a response reinjector that resolves every response's adapter through
    /// <paramref name="adapterTargets"/> (the refreshable capture-scope snapshot: the host
    /// fallback target plus a stable-ID → target map so a flow's response can be sent toward
    /// its origin adapter). <paramref name="maximumFrameSize"/> is the pinned NDISAPI frame cap
    /// (default 1514, or 9014 for a jumbo-capable ABI) that bounds rebuilt frames (M3).
    /// <paramref name="bufferPool"/> supplies the native buffers responses are built into
    /// (pooled reuse instead of a per-response allocation).
    /// </summary>
    public UdpResponseReinjector(
        IPacketReinjector reinjector,
        IUdpAdapterTargetSource adapterTargets,
        int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame,
        IRuntimeLogger? logger = null,
        NdisPacketBufferPool? bufferPool = null)
    {
        ArgumentNullException.ThrowIfNull(reinjector);
        ArgumentNullException.ThrowIfNull(adapterTargets);
        if (maximumFrameSize <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFrameSize));
        _reinjector = reinjector;
        _adapterTargets = adapterTargets;
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
    /// Resolves the reinjection target and the Ethernet destination MAC for a response, consulting
    /// the adapter-target source per response. Host flows reinject toward MSTCP on their capture
    /// adapter with that adapter's MAC on both header slots; a missing capture adapter falls back
    /// to the source's host target, dropping fail-closed when no host target exists either.
    /// Forwarded flows reinject toward the origin adapter (H2) with the origin adapter's MAC as
    /// source and the recorded client MAC as destination so the vSwitch delivers to the VM instead
    /// of the host stack. A forwarded flow with an unresolved origin adapter or without a recorded
    /// client MAC is dropped fail-closed with a rate-limited log rather than sent out the wrong
    /// adapter.
    /// </summary>
    private bool TryResolveTarget(FlowKey originalFlow, byte[]? clientMac, out UdpAdapterTarget target, out bool towardMstcp, out byte[]? destinationMac)
    {
        towardMstcp = true;
        if (originalFlow.Origin != FlowOriginKind.Forwarded)
        {
            if (originalFlow.OriginAdapterId is { } originAdapterId && _adapterTargets.Resolve(originAdapterId) is { } hostOriginAdapter)
            {
                target = hostOriginAdapter;
                destinationMac = target.Mac;
                return true;
            }
            if (_adapterTargets.Host is { } hostTarget)
            {
                LogHostAdapterFallback();
                target = hostTarget;
                destinationMac = target.Mac;
                return true;
            }
            target = default;
            destinationMac = null;
            if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogTrace("udp.response.dropped", originalFlow, new RuntimeLogField("reason", "missingHostTarget"));
            LogMissingHostTarget();
            return false;
        }

        if (originalFlow.OriginAdapterId is null || _adapterTargets.Resolve(originalFlow.OriginAdapterId) is not { } originAdapter)
        {
            target = default;
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
            _logger.Warn("Host UDP response origin adapter is not resolved in the reinjection map; using the host fallback adapter.");
        }
    }

    private void LogMissingHostTarget()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastMissingHostTargetLogTicks);
        if (now - last >= MissingOriginLogInterval.Ticks && Interlocked.CompareExchange(ref _lastMissingHostTargetLogTicks, now, last) == last)
        {
            _logger.Warn("Host UDP response dropped fail-closed: no host fallback adapter is currently available.");
        }
    }
}
