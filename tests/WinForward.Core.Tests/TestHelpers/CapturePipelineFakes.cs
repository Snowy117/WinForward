using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using WinForward.Windows;

namespace WinForward.Core.Tests;

/// <summary>
/// Dispatcher/executor fakes shared by the capture-pipeline test files: a never-owning
/// self-traffic guard, a counting process attributor, counting and throwing action executors,
/// and throwing TCP redirect collaborators that prove a proxy path never allocates.
/// </summary>
internal sealed class FakeGuard : ISelfTrafficGuard
{
    public bool IsOwned(FlowContext context) => false;
}

internal sealed class FakeAttributor : IProcessAttributor
{
    private readonly string? _name;
    public int Calls { get; private set; }
    public FakeAttributor(string? name) => _name = name;
    public ValueTask<ProcessIdentity?> FindAsync(FlowKey key, CancellationToken cancellationToken)
    {
        Calls++;
        return ValueTask.FromResult<ProcessIdentity?>(_name is null ? null : new ProcessIdentity(123, DateTime.UtcNow, _name, null));
    }
}

internal sealed class FakeExecutor : IPacketActionExecutor
{
    public int PassCount { get; private set; }
    public int BlockCount { get; private set; }
    public int ProxyCount { get; private set; }
    public ValueTask PassAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) { PassCount++; return ValueTask.CompletedTask; }
    public ValueTask BlockAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) { BlockCount++; return ValueTask.CompletedTask; }
    public ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken) { ProxyCount++; return ValueTask.CompletedTask; }
}

internal sealed class ThrowingPassExecutor : IPacketActionExecutor
{
    public ValueTask PassAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) => throw new InvalidOperationException("injection failed");
    public ValueTask BlockAsync(CapturedFlowPacket packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed class ThrowingRedirectListenerFactory : ITcpRedirectListenerFactory
{
    public ValueTask<ITcpRedirectListener> CreateAsync(AddressFamilyKind addressFamily, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("listener allocation is not expected on this path");
}

internal sealed class ThrowingRelayFactory : ITcpProxyRelayFactory
{
    public ValueTask<ITcpRelay> EstablishAsync(Endpoint originalDestination, ITcpAcceptedConnection acceptedConnection, Socks5Server server, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("relay setup is not expected on this path");
}

internal sealed class ThrowingRedirectInjector : ITcpRedirectInjector
{
    public ValueTask InjectAsync(ReadOnlyMemory<byte> rewrittenFrame, bool towardMstcp, nint adapterHandle, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("injection is not expected on this path");
}
