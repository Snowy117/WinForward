using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Core.Tests;

/// <summary>
/// Process-wide native pools and one shared setup executor for coordinator construction in tests
/// (Phase A / R6): a coordinator borrows its pools and executor from composition and never
/// disposes them, so tests that do not assert pool behavior share these long-lived instances
/// instead of owning a per-test one. The shared UDP pools are sized for the largest frame cap any
/// test pins; tests that assert pool accounting pass their own pool.
/// </summary>
internal static class TestPools
{
    /// <summary>Largest frame cap a test pins on a coordinator; shared UDP pools must fit every default-sized site.</summary>
    private const int MaximumTestFrameSize = 4096;

    internal static NativeBufferPool SynCopyPool { get; } = new(NdisApiAbi.MaximumEthernetFrame);

    internal static NativeBufferPool UdpSetupQueuePool { get; } = new(MaximumTestFrameSize);

    internal static NativeBufferPool UdpReceiveWindowPool { get; } = new(UdpProxyCoordinator.ReceiveWindowSize(MaximumTestFrameSize));

    internal static ISetupExecutor SetupExecutor { get; } = new SetupExecutor();
}
