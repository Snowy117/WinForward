using System.Runtime.InteropServices;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Windows;

namespace WinForward.Runtime;

/// <summary>
/// The native capture metadata a reinjection executor needs to decide where a passed frame returns:
/// the packet's MSS/NDISAPI direction flag and the adapter handle it was captured on.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct PacketCaptureMetadata(uint DeviceFlags, nint AdapterHandle)
{
    public bool IsOnSend => (DeviceFlags & NdisApiAbi.PacketFlagOnSend) != 0;
}

public sealed record CapturedFlowPacket(PacketLease Lease, FlowContext Context, PacketCaptureMetadata Metadata = default);

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

public interface IRuntimeLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

public sealed class NullRuntimeLogger : IRuntimeLogger
{
    public static readonly NullRuntimeLogger Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
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

    public FlowDispatcher(ValidatedConfiguration configuration, ISelfTrafficGuard selfTraffic, IPacketActionExecutor executor, IProcessAttributor? attributor = null, int flowCapacity = 65_536, Func<CapturedFlowPacket, CancellationToken, ValueTask<TcpRedirectOutcome>>? reverseHandler = null)
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
    }

    public async ValueTask DispatchAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_selfTraffic.IsOwned(packet.Context))
        {
            await CompleteAsync(packet, PacketDisposition.Pass, () => _executor.PassAsync(packet, cancellationToken)).ConfigureAwait(false);
            return;
        }

        // A packet on an active redirect leg (source or destination port is a proxy listener port)
        // must be reversed back to the original server:client tuple before the Windows stack sees it.
        // This runs before flow lookup and policy so a reverse packet is never re-evaluated as a new
        // client flow or silently passed.
        if (_reverseHandler is not null)
        {
            var proxyOutcome = await _reverseHandler(packet, cancellationToken).ConfigureAwait(false);
            if (proxyOutcome == TcpRedirectOutcome.Injected)
            {
                await CompleteAsync(packet, PacketDisposition.ProxyConsumed, () => ValueTask.CompletedTask).ConfigureAwait(false);
                return;
            }
            if (proxyOutcome == TcpRedirectOutcome.Blocked)
            {
                await CompleteAsync(packet, PacketDisposition.Block, () => _executor.BlockAsync(packet, cancellationToken)).ConfigureAwait(false);
                return;
            }
        }

        if (_flows.TryResolve(packet.Context.Key, out var existing) && existing is not null)
        {
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
        if (_attributor is not null && context.ProcessName is null && context.ProcessPath is null && context.Key.Origin == FlowOriginKind.Host)
        {
            var identity = await _attributor.FindAsync(context.Key, cancellationToken).ConfigureAwait(false);
            if (identity is not null) context = context with { ProcessName = identity.Value.Name, ProcessPath = identity.Value.FullPath };
        }

        if (!_flows.TryClaimResolved(context.Key, () => _policy.Evaluate(context), out var claimed) || claimed is null)
        {
            await CompleteAsync(packet, PacketDisposition.Block, () => _executor.BlockAsync(packet, cancellationToken)).ConfigureAwait(false);
            return;
        }

        await ExecuteDecisionAsync(packet, claimed.Decision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a frame that cannot be classified into a TCP/UDP flow (non-IP, fragmented, malformed,
    /// or a non-TCP/UDP protocol). Such traffic cannot match a TCP/UDP-only rule; it is evaluated
    /// against the rules that can meaningfully match it (adapter-only rules) and otherwise the
    /// fallback action. It is not cached in the flow table because these frames have no logical flow.
    /// </summary>
    public async ValueTask DispatchNonFlowAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_selfTraffic.IsOwned(packet.Context))
        {
            await CompleteAsync(packet, PacketDisposition.Pass, () => _executor.PassAsync(packet, cancellationToken)).ConfigureAwait(false);
            return;
        }

        var action = EvaluateNonFlow(packet.Context);
        if (action == FlowAction.Pass)
        {
            await CompleteAsync(packet, PacketDisposition.Pass, () => _executor.PassAsync(packet, cancellationToken)).ConfigureAwait(false);
        }
        else
        {
            await CompleteAsync(packet, PacketDisposition.Block, () => _executor.BlockAsync(packet, cancellationToken)).ConfigureAwait(false);
        }
    }

    private FlowAction EvaluateNonFlow(FlowContext context)
    {
        foreach (var rule in _policy.Rules)
        {
            var matcher = rule.Matcher;
            if (matcher.Processes is not null || matcher.Protocols is not null || matcher.AddressFamilies is not null || matcher.RemoteNetworks is not null || matcher.RemotePorts is not null) continue;
            var adapterMatches = (matcher.AdapterIds is null || matcher.AdapterIds.Contains(context.AdapterId ?? string.Empty)) &&
                (matcher.AdapterNames is null || matcher.AdapterNames.Contains(context.AdapterName ?? string.Empty));
            if (adapterMatches) return rule.Decision.Action;
        }

        return _policy.FallbackAction;
    }

    private static bool IsReverseOf(FlowKey stored, FlowKey observed) =>
        stored.AddressFamily == observed.AddressFamily &&
        stored.Protocol == observed.Protocol &&
        stored.Local == observed.Remote &&
        stored.Remote == observed.Local;

    private async ValueTask ExecuteDecisionAsync(CapturedFlowPacket packet, FlowDecision decision, CancellationToken cancellationToken)
    {
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

    private static async ValueTask CompleteAsync(CapturedFlowPacket packet, PacketDisposition disposition, Func<ValueTask> execute)
    {
        if (!packet.Lease.TryComplete(disposition)) return;
        await execute().ConfigureAwait(false);
    }
}