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
    ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken cancellationToken);
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
    private static readonly TimeSpan s_missingOriginLogInterval = TimeSpan.FromSeconds(5);

    /// <summary>Throttle window for the structured reinjection diagnostics: these failures can
    /// repeat at query rate, and the drop shape is an aggregate staleness signal, so one line per
    /// window plus the counter suffices.</summary>
    private static readonly TimeSpan s_structuredReinjectLogInterval = TimeSpan.FromSeconds(30);

    private readonly IPacketReinjector _reinjector;
    private readonly IUdpAdapterTargetSource _adapterTargets;
    private readonly NdisPacketBufferPool _bufferPool;
    private readonly int _maximumFrameSize;
    private readonly IRuntimeLogger _logger;
    private readonly IInterceptionHealthSignal _healthSignal;
    private readonly RuntimeLogThrottle _originUnresolvedWarn = new(s_structuredReinjectLogInterval);
    private readonly RuntimeLogThrottle _failClosedDropWarn = new(s_structuredReinjectLogInterval);
    private long _lastMissingClientMacLogTicks;
    private long _lastFrameBuildFailureLogTicks;

    /// <summary>
    /// Creates a response reinjector that resolves every response's adapter through
    /// <paramref name="adapterTargets"/> (the refreshable capture-scope snapshot: the host
    /// fallback target plus a stable-ID → target map so a flow's response can be sent toward
    /// its origin adapter). <paramref name="maximumFrameSize"/> is the pinned NDISAPI frame cap
    /// (default 1514, or 9014 for a jumbo-capable ABI) that bounds rebuilt frames (M3).
    /// <paramref name="bufferPool"/> supplies the native buffers responses are built into
    /// (pooled reuse instead of a per-response allocation). <paramref name="healthSignal"/>
    /// receives the staleness-shaped failures (host fallback and fail-closed drops) that the
    /// capture runner may answer with a forced adapter-view refresh; null means no-op.
    /// </summary>
    public UdpResponseReinjector(
        IPacketReinjector reinjector,
        IUdpAdapterTargetSource adapterTargets,
        int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame,
        IRuntimeLogger? logger = null,
        NdisPacketBufferPool? bufferPool = null,
        IInterceptionHealthSignal? healthSignal = null)
    {
        ArgumentNullException.ThrowIfNull(reinjector);
        ArgumentNullException.ThrowIfNull(adapterTargets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrameSize);
        _reinjector = reinjector;
        _adapterTargets = adapterTargets;
        _maximumFrameSize = maximumFrameSize;
        _logger = logger ?? NullRuntimeLogger.Instance;
        _bufferPool = bufferPool ?? NdisPacketBufferPool.Shared;
        _healthSignal = healthSignal ?? InterceptionHealthMonitor.Noop;
    }

    public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken cancellationToken)
    {
        if (!TryResolveTarget(originalFlow, clientMac, out var target, out var towardMstcp))
        {
            return ValueTask.CompletedTask;
        }

        var buffer = _bufferPool.Rent();
        try
        {
            // The frame is built in place into the pooled native buffer (R4): no managed byte[]
            // per response on the steady path.
            if (!TryBuildResponseFrame(originalFlow, remoteSource, payload, target, towardMstcp, clientMac, buffer.GetFrameStorage(), out var frameLength))
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
    /// Builds one response frame into <paramref name="destination"/>: host flows put the resolved
    /// adapter MAC on both header slots, forwarded flows write the inline-recorded client MAC into
    /// a stack buffer as the destination (no managed copy of the captured header bytes).
    /// </summary>
    private bool TryBuildResponseFrame(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, UdpAdapterTarget target, bool towardMstcp, MacAddress clientMac, Span<byte> destination, out int frameLength)
    {
        Span<byte> destinationMac = stackalloc byte[MacAddress.Length];
        if (towardMstcp) destinationMac = target.Mac;
        else clientMac.CopyTo(destinationMac);
        return UdpFrameBuilder.TryBuildInto(
            remoteSource.Address,
            remoteSource.Port,
            originalFlow.Local.Address,
            originalFlow.Local.Port,
            payload,
            target.Mac,
            destinationMac,
            destination,
            out frameLength,
            _maximumFrameSize);
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
    private bool TryResolveTarget(FlowKey originalFlow, MacAddress clientMac, out UdpAdapterTarget target, out bool towardMstcp)
    {
        towardMstcp = true;
        if (originalFlow.Origin != FlowOriginKind.Forwarded)
        {
            if (originalFlow.OriginAdapterId is { } originAdapterId && _adapterTargets.Resolve(originAdapterId) is { } hostOriginAdapter)
            {
                target = hostOriginAdapter;
                return true;
            }
            if (_adapterTargets.Host is { } hostTarget)
            {
                LogHostAdapterFallback(originalFlow);
                target = hostTarget;
                return true;
            }
            target = default;
            if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogTrace("udp.response.dropped", originalFlow, new RuntimeLogField("reason", "missingHostTarget"));
            LogMissingHostTarget(originalFlow);
            return false;
        }

        if (originalFlow.OriginAdapterId is null || _adapterTargets.Resolve(originalFlow.OriginAdapterId) is not { } originAdapter)
        {
            target = default;
            if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogTrace("udp.response.dropped", originalFlow, new RuntimeLogField("reason", "missingOriginAdapter"));
            LogMissingOriginAdapter(originalFlow);
            return false;
        }
        target = originAdapter;
        towardMstcp = false;
        if (!clientMac.IsValid)
        {
            if (_logger.IsEnabled(RuntimeLogLevel.Trace)) LogTrace("udp.response.dropped", originalFlow, new RuntimeLogField("reason", "missingClientMac"));
            LogMissingClientMac();
            return false;
        }
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
        if (now - last >= s_missingOriginLogInterval.Ticks && Interlocked.CompareExchange(ref _lastFrameBuildFailureLogTicks, now, last) == last)
        {
            _logger.Warn("UDP response frame build failed; dropping the response (fail-closed).");
        }
    }

    private void LogMissingOriginAdapter(FlowKey originalFlow)
    {
        LogFailClosedDrop(originalFlow, "missingOriginAdapter");
    }

    private void LogMissingClientMac()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastMissingClientMacLogTicks);
        if (now - last >= s_missingOriginLogInterval.Ticks && Interlocked.CompareExchange(ref _lastMissingClientMacLogTicks, now, last) == last)
        {
            _logger.Warn("Forwarded UDP response dropped fail-closed: the flow's client MAC was not recorded.");
        }
    }

    private void LogHostAdapterFallback(FlowKey originalFlow)
    {
        RuntimeCounters.Shared.Increment(RuntimeCounters.UdpOriginUnresolved);
        _healthSignal.ReportFailure(RuntimeCounters.UdpOriginUnresolved);
        if (!_originUnresolvedWarn.ShouldEmit() || !_logger.IsEnabled(RuntimeLogLevel.Warn)) return;
        _logger.Event(RuntimeLogLevel.Warn, "udp.reinject.unresolved",
            new("source", originalFlow.Local),
            new("destination", originalFlow.Remote),
            new("originAdapter", originalFlow.OriginAdapterId),
            new("mapAdapters", string.Join(',', _adapterTargets.AdapterIds)),
            new("fallback", "host"));
    }

    private void LogMissingHostTarget(FlowKey originalFlow)
    {
        LogFailClosedDrop(originalFlow, "missingHostTarget");
    }

    /// <summary>
    /// The structured fail-closed-drop warn (<c>udp.reinject.drop</c>): throttled to one line per
    /// window with the flow key and origin kind, always counted. The drop itself is unchanged —
    /// the response is never sent out an adapter it cannot belong to.
    /// </summary>
    private void LogFailClosedDrop(FlowKey originalFlow, string reason)
    {
        RuntimeCounters.Shared.Increment(RuntimeCounters.UdpFailClosedDrop);
        _healthSignal.ReportFailure(RuntimeCounters.UdpFailClosedDrop);
        if (!_failClosedDropWarn.ShouldEmit() || !_logger.IsEnabled(RuntimeLogLevel.Warn)) return;
        _logger.Event(RuntimeLogLevel.Warn, "udp.reinject.drop",
            new("source", originalFlow.Local),
            new("destination", originalFlow.Remote),
            new("originKind", originalFlow.Origin),
            new("originAdapter", originalFlow.OriginAdapterId),
            new("reason", reason));
    }
}
