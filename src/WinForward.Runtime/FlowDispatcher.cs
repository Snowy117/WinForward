using WinForward.Configuration;
using WinForward.Core;

namespace WinForward.Runtime;

public sealed record CapturedFlowPacket(PacketLease Lease, FlowContext Context);

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

    public FlowDispatcher(ValidatedConfiguration configuration, ISelfTrafficGuard selfTraffic, IPacketActionExecutor executor, int flowCapacity = 65_536)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(selfTraffic);
        ArgumentNullException.ThrowIfNull(executor);
        _policy = configuration.Policy;
        _servers = configuration.Servers;
        _flows = new FlowTable(flowCapacity);
        _selfTraffic = selfTraffic;
        _executor = executor;
    }

    public async ValueTask DispatchAsync(CapturedFlowPacket packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_selfTraffic.IsOwned(packet.Context))
        {
            await CompleteAsync(packet, PacketDisposition.Pass, () => _executor.PassAsync(packet, cancellationToken)).ConfigureAwait(false);
            return;
        }

        if (_flows.TryGet(packet.Context.Key, out var existing) && existing is not null)
        {
            await ExecuteDecisionAsync(packet, existing.Decision, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_flows.TryClaim(packet.Context.Key, () => _policy.Evaluate(packet.Context), out var claimed) || claimed is null)
        {
            await CompleteAsync(packet, PacketDisposition.Block, () => _executor.BlockAsync(packet, cancellationToken)).ConfigureAwait(false);
            return;
        }

        await ExecuteDecisionAsync(packet, claimed.Decision, cancellationToken).ConfigureAwait(false);
    }

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
