using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using WinForward.Runtime.UdpProxy;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// R6 ownership consolidation: a coordinator borrows its native pools and setup executor from
/// composition and must never dispose them, and repeated disposal is single-flight.
/// </summary>
public sealed class CoordinatorOwnershipTests
{
    [Fact]
    public async Task TcpCoordinatorDisposeDoesNotDisposeInjectedCollaboratorsAndIsSingleFlight()
    {
        using var synCopyPool = new NativeBufferPool(NdisApiAbi.MaximumEthernetFrame, capacity: 4);
        var warmLease = synCopyPool.Rent();
        warmLease.Dispose();
        Assert.Equal(1, synCopyPool.Count);

        using var executor = new CountingSetupExecutor();
        var coordinator = TcpCoordinatorFakes.CreateCoordinator(
            new FakeListenerFactory(),
            new FakeRelayFactory(),
            new FakeInjector(),
            new TcpRedirectTable(),
            new SelfTrafficRegistry(),
            new FakeLocalAddressProvider(),
            synCopyPool: synCopyPool,
            setupExecutor: executor);

        var firstDispose = AsTask(coordinator.DisposeAsync());
        var secondDispose = AsTask(coordinator.DisposeAsync());
        Assert.Same(firstDispose, secondDispose);

        await secondDispose;

        Assert.Equal(0, executor.DisposeCount);
        Assert.Equal(1, synCopyPool.Count);
    }

    [Fact]
    public async Task UdpCoordinatorDisposeDoesNotDisposeInjectedCollaborators()
    {
        using var setupQueuePool = new NativeBufferPool(1500, capacity: 4);
        using var receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(1500), capacity: 4);
        setupQueuePool.Rent().Dispose();
        receiveWindowPool.Rent().Dispose();
        Assert.Equal(1, setupQueuePool.Count);
        Assert.Equal(1, receiveWindowPool.Count);

        using var executor = new CountingSetupExecutor();
        var coordinator = UdpCoordinatorFakes.CreateCoordinator(
            new FakeTransportFactory(),
            new FakeResponseSink(),
            setupQueuePool: setupQueuePool,
            receiveWindowPool: receiveWindowPool,
            setupExecutor: executor);

        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();

        Assert.Equal(0, executor.DisposeCount);
        Assert.Equal(1, setupQueuePool.Count);
        Assert.Equal(1, receiveWindowPool.Count);
    }

    private static Task AsTask(ValueTask value) => value.AsTask();

    private sealed class CountingSetupExecutor : ISetupExecutor
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public SetupWorkItem RentItem(Func<SetupWorkItem, Task> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            throw new NotSupportedException("The ownership tests never exercise setup work.");
        }

        public bool TryEnqueue(SetupWorkItem item)
        {
            ArgumentNullException.ThrowIfNull(item);
            throw new NotSupportedException("The ownership tests never exercise setup work.");
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
