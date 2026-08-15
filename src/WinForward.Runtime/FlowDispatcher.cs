using System.Runtime.InteropServices;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Windows;

namespace WinForward.Runtime;

/// <summary>
/// The native capture metadata a reinjection executor needs to decide where a passed frame returns:
/// the NDISAPI direction flag, packet metadata flags, and the adapter handle it was captured on.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct PacketCaptureMetadata(uint DeviceFlags, nint AdapterHandle, uint Flags = 0)
{
    public bool IsOnSend => (DeviceFlags & NdisApiAbi.PacketFlagOnSend) != 0;
}

public sealed record CapturedFlowPacket(
    PacketLease Lease,
    FlowContext Context,
    PacketCaptureMetadata Metadata = default,
    long PacketSequence = 0,
    long FlowGeneration = 0);

public interface ISelfTrafficGuard
{
    bool IsOwned(FlowContext context);
}

public interface IPacketActionExecutor
{
    ValueTask PassAsync(CapturedFlowPacket packet, CancellationToken cancellationToken);
    ValueTask BlockAsync(CapturedFlowPacket packet, CancellationToken cancellationToken);
    ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken);
}

public sealed class FlowDispatcher
{
    private readonly PolicySnapshot _policy;
    private readonly IReadOnlyDictionary<string, Socks5Server> _servers;
    private readonly FlowTable _flows;
    private readonly ISelfTrafficGuard _selfTraffic;
    private readonly IPacketActionExecutor _executor;
    private readonly IProcessAttributor? _attributor;
    private readonly Func<CapturedFlowPacket, CancellationToken, ValueTask<TcpRedirectOutcome>>? _reverseHandler;
    private readonly IRuntimeLogger _logger;
    private readonly bool _includeProcessPathInLogs;

    public FlowDispatcher(ValidatedConfiguration configuration, ISelfTrafficGuard selfTraffic, IPacketActionExecutor executor, IProcessAttributor? attributor = null, int flowCapacity = 65_536, Func<CapturedFlowPacket, CancellationToken, ValueTask<TcpRedirectOutcome>>? reverseHandler = null, IRuntimeLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(selfTraffic);
        ArgumentNullException.ThrowIfNull(executor);
        _policy = configuration.Policy;
        _servers = configuration.Servers;
        _flows = new FlowTable(flowCapacity);
        _selfTraffic = selfTraffic;
        _executor = executor;
        _attributor = attributor;
        _reverseHandler = reverseHandler;
        _logger = logger ?? NullRuntimeLogger.Instance;
        _includeProcessPathInLogs = configuration.IncludeProcessPathInLogs;
    }

    /// <summary>
    /// Removes flow decisions idle past <paramref name="idleTimeout"/> so the bounded flow table
    /// does not accumulate stale one-shot flows (design §7). The runtime calls this on a periodic
    /// sweep; active flows keep their decisions because observations touch them.
    /// </summary>
    public int RemoveExpiredFlows(DateTimeOffset now, TimeSpan idleTimeout) => _flows.RemoveExpired(now, idleTimeout);

    public async ValueTask DispatchAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        LogPacketStage(RuntimeLogLevel.Trace, "packet.classified", packet, new RuntimeLogField("kind", "flow"));
        if (await TryHandleSelfTrafficAsync(packet, cancellationToken).ConfigureAwait(false)) return;

        // A packet on an active TCP redirect leg (a port matches a proxy listener port) must be
        // reversed back to the original server:client tuple before the Windows stack sees it. This
        // runs before flow lookup and policy so a reverse packet is never re-evaluated as a new
        // client flow. Gated on TCP only (H1/M5): the reverse handler keys on numeric port alone,
        // so a UDP datagram whose port collides with a TCP listener port must never reach it.
        if (await TryHandleReverseAsync(packet, cancellationToken).ConfigureAwait(false)) return;

        if (_flows.TryResolve(packet.Context.Key, out var existing) && existing is not null)
        {
            packet = packet with { FlowGeneration = existing.Generation };
            LogPacketStage(RuntimeLogLevel.Trace, "packet.flowResolved", packet, new RuntimeLogField("existing", true));
            // A UDP proxy response (server -> client on an actively proxied flow) is injected by
            // UdpResponseReinjector toward the local stack; when the capture path observes it again
            // it must be delivered to the client, not re-proxied back to the relay. Detect by
            // direction: the stored flow key is (client:port -> server:port), a response is the
            // reverse. TCP reverse packets are handled by the reverse hook before this point.
            if (existing.Decision.Action == FlowAction.Proxy && packet.Context.Key.Protocol == TransportProtocol.Udp &&
                IsReverseOf(existing.Key, packet.Context.Key))
            {
                await CompleteAsync(packet, PacketDisposition.Pass, () => _executor.PassAsync(packet, cancellationToken)).ConfigureAwait(false);
                return;
            }
            await ExecuteDecisionAsync(packet, existing.Decision, cancellationToken).ConfigureAwait(false);
            return;
        }

        var context = packet.Context;
        context = await AttributeProcessAsync(context, cancellationToken).ConfigureAwait(false);

        if (!_flows.TryClaimResolved(context.Key, () => EvaluateNewFlow(context), out var claimed) || claimed is null)
        {
            LogPacketStage(RuntimeLogLevel.Trace, "packet.flowResolved", packet, new RuntimeLogField("outcome", "capacity"));
            await CompleteAsync(packet, PacketDisposition.Block, () => _executor.BlockAsync(packet, cancellationToken)).ConfigureAwait(false);
            return;
        }

        packet = packet with { Context = context, FlowGeneration = claimed.Generation };
        LogPacketStage(RuntimeLogLevel.Trace, "packet.flowResolved", packet, new RuntimeLogField("existing", false));
        if (_logger.IsEnabled(RuntimeLogLevel.Debug))
        {
            var fields = FlowFields(packet, 3);
            fields[^3] = new("action", claimed.Decision.Action);
            fields[^2] = new("rule", claimed.Decision.RuleIndex);
            fields[^1] = new("proxy", claimed.Decision.ProxyServerName);
            _logger.Event(RuntimeLogLevel.Debug, "flow.created", fields);
        }
        await ExecuteDecisionAsync(packet, claimed.Decision, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> TryHandleSelfTrafficAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        if (!_selfTraffic.IsOwned(packet.Context)) return false;
        LogPacketStage(RuntimeLogLevel.Trace, "packet.selfTraffic", packet, new RuntimeLogField("outcome", "pass"));
        await CompleteAsync(packet, PacketDisposition.Pass, () => _executor.PassAsync(packet, cancellationToken)).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<bool> TryHandleReverseAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        if (_reverseHandler is null || packet.Context.Key.Protocol != TransportProtocol.Tcp) return false;
        var outcome = await _reverseHandler(packet, cancellationToken).ConfigureAwait(false);
        if (outcome == TcpRedirectOutcome.NotRelevant) return false;
        var disposition = outcome == TcpRedirectOutcome.Injected ? PacketDisposition.ProxyConsumed : PacketDisposition.Block;
        LogPacketStage(RuntimeLogLevel.Trace, "packet.reverseHandled", packet, new RuntimeLogField("outcome", outcome));
        await CompleteAsync(packet, disposition, disposition == PacketDisposition.Block
            ? () => _executor.BlockAsync(packet, cancellationToken)
            : () => ValueTask.CompletedTask).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<FlowContext> AttributeProcessAsync(FlowContext context, CancellationToken cancellationToken)
    {
        if (_attributor is null || context.ProcessName is not null || context.ProcessPath is not null || context.Key.Origin != FlowOriginKind.Host) return context;
        var identity = await _attributor.FindAsync(context.Key, cancellationToken).ConfigureAwait(false);
        return identity is null ? context : context with { ProcessName = identity.Value.Name, ProcessPath = identity.Value.FullPath };
    }

    /// <summary>
    /// Handles a frame that cannot be classified into a TCP/UDP flow (non-IP, fragmented, malformed,
    /// or a non-TCP/UDP protocol). Such frames can never enter a proxy relay, so policy does not
    /// apply to them: they are always passed. Blocking them blackholes ARP, ICMP/ND, and PMTUD and
    /// severs the very connectivity proxied flows depend on. The frame is not cached in the flow
    /// table because these frames have no logical flow.
    /// </summary>
    public async ValueTask DispatchNonFlowAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        LogPacketStage(RuntimeLogLevel.Trace, "packet.classified", packet, new RuntimeLogField("kind", "nonFlow"));
        if (_selfTraffic.IsOwned(packet.Context))
        {
            LogPacketStage(RuntimeLogLevel.Trace, "packet.selfTraffic", packet, new RuntimeLogField("outcome", "pass"));
            await CompleteAsync(packet, PacketDisposition.Pass, () => _executor.PassAsync(packet, cancellationToken)).ConfigureAwait(false);
            return;
        }

        LogPacketStage(RuntimeLogLevel.Trace, "packet.action", packet, new RuntimeLogField("action", FlowAction.Pass), new RuntimeLogField("rule", null), new RuntimeLogField("proxy", null), new RuntimeLogField("reason", "nonFlow"));
        await CompleteAsync(packet, PacketDisposition.Pass, () => _executor.PassAsync(packet, cancellationToken)).ConfigureAwait(false);
    }

    private FlowDecision EvaluateNewFlow(FlowContext context) =>
        context.Key.Origin == FlowOriginKind.Forwarded ? _policy.EvaluateForwarded(context) : _policy.Evaluate(context);

    private static bool IsReverseOf(FlowKey stored, FlowKey observed) =>
        stored.AddressFamily == observed.AddressFamily &&
        stored.Protocol == observed.Protocol &&
        stored.Local == observed.Remote &&
        stored.Remote == observed.Local;

    private async ValueTask ExecuteDecisionAsync(CapturedFlowPacket packet, FlowDecision decision, CancellationToken cancellationToken)
    {
        LogPacketStage(RuntimeLogLevel.Trace, "packet.action", packet, new RuntimeLogField("action", decision.Action), new RuntimeLogField("rule", decision.RuleIndex), new RuntimeLogField("proxy", decision.ProxyServerName));
        switch (decision.Action)
        {
            case FlowAction.Pass:
                await CompleteAsync(packet, PacketDisposition.Pass, () => _executor.PassAsync(packet, cancellationToken)).ConfigureAwait(false);
                return;
            case FlowAction.Block:
                await CompleteAsync(packet, PacketDisposition.Block, () => _executor.BlockAsync(packet, cancellationToken)).ConfigureAwait(false);
                return;
            case FlowAction.Proxy when decision.ProxyServerName is not null && _servers.TryGetValue(decision.ProxyServerName, out var server):
                await CompleteAsync(packet, PacketDisposition.ProxyConsumed, () => _executor.ProxyAsync(packet, server, cancellationToken)).ConfigureAwait(false);
                return;
            default:
                await CompleteAsync(packet, PacketDisposition.Block, () => _executor.BlockAsync(packet, cancellationToken)).ConfigureAwait(false);
                return;
        }
    }

    private async ValueTask CompleteAsync(CapturedFlowPacket packet, PacketDisposition disposition, Func<ValueTask> execute)
    {
        if (!packet.Lease.TryComplete(disposition)) return;
        await execute().ConfigureAwait(false);
        LogPacketStage(RuntimeLogLevel.Trace, "packet.completed", packet, new RuntimeLogField("disposition", disposition));
    }

    private void LogPacketStage(RuntimeLogLevel level, string eventName, CapturedFlowPacket packet, params RuntimeLogField[] fields)
    {
        if (!_logger.IsEnabled(level)) return;
        var allFields = new RuntimeLogField[fields.Length + 8];
        allFields[0] = new("packet", packet.PacketSequence == 0 ? null : packet.PacketSequence);
        allFields[1] = new("flow", packet.FlowGeneration == 0 ? null : packet.FlowGeneration);
        fields.CopyTo(allFields, 2);
        allFields[fields.Length + 2] = new("protocol", packet.Context.Key.Protocol);
        allFields[fields.Length + 3] = new("origin", packet.Context.Key.Origin);
        allFields[fields.Length + 4] = new("source", packet.Context.Key.Local);
        allFields[fields.Length + 5] = new("destination", packet.Context.Key.Remote);
        allFields[fields.Length + 6] = new("process", packet.Context.ProcessName);
        allFields[fields.Length + 7] = new("processPath", _includeProcessPathInLogs ? packet.Context.ProcessPath : null);
        _logger.Event(level, eventName, allFields);
    }

    private RuntimeLogField[] FlowFields(CapturedFlowPacket packet, int additionalFields)
    {
        var fields = new RuntimeLogField[7 + additionalFields];
        fields[0] = new("flow", packet.FlowGeneration == 0 ? null : packet.FlowGeneration);
        fields[1] = new("protocol", packet.Context.Key.Protocol);
        fields[2] = new("origin", packet.Context.Key.Origin);
        fields[3] = new("source", packet.Context.Key.Local);
        fields[4] = new("destination", packet.Context.Key.Remote);
        fields[5] = new("process", packet.Context.ProcessName);
        fields[6] = new("processPath", _includeProcessPathInLogs ? packet.Context.ProcessPath : null);
        return fields;
    }
}
