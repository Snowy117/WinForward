using System.Net;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.TcpRedirect;
using WinForward.TestSupport;
using WinForward.Windows;
using Xunit;
using static WinForward.TestSupport.TcpCoordinatorFakes;

namespace WinForward.Integration.Tests;

/// <summary>
/// Header-walk counts for a redirected packet, driven through the real
/// <see cref="CapturePacketProcessor.ProcessAsync"/> → <see cref="FlowDispatcher"/> →
/// <see cref="TcpProxyCoordinator"/> path on both legs. The processor's
/// <see cref="IPTcpUdpPacket.TryParse"/> is the first walk, so a harness that starts at the
/// coordinator cannot observe the whole count; the sinks are <c>[ThreadStatic]</c>, so every fact
/// drives, counts and asserts on one thread and asserts the managed thread id did not change.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PacketPathWalkCountTests
{
    private static readonly IPAddress s_clientIPv4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destIPv4 = IPAddress.Parse("192.0.2.53");
    private static readonly Socks5Server s_server = new("primary", "127.0.0.1", 1080, Username: null, Password: null);
    private const nint AdapterHandle = 0x1234;

    [Fact]
    public async Task RedirectedForwardPacketWalksHeadersExactlyOnce()
    {
        await using var composition = await Composition.CreateAsync();
        const int count = 16;

        var measuredThreadId = Environment.CurrentManagedThreadId;
        var parse = new WalkCounter();
        var revalidate = new WalkCounter();
        var viewRewrite = new WalkCounter();
        PacketPathProbe.ParseWalk = parse.Bump;
        PacketPathProbe.RevalidateWalk = revalidate.Bump;
        PacketPathProbe.ViewRewrite = viewRewrite.Bump;
        try
        {
            for (var index = 0; index < count; index++) await composition.DriveForwardMidFlowAsync();
        }
        finally
        {
            PacketPathProbe.ParseWalk = null;
            PacketPathProbe.RevalidateWalk = null;
            PacketPathProbe.ViewRewrite = null;
        }

        Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
        Assert.Equal(count, parse.Count);
        Assert.Equal(0, revalidate.Count);
        Assert.Equal(count, viewRewrite.Count);
    }

    [Fact]
    public async Task RedirectedReversePacketWalksHeadersExactlyOnce()
    {
        await using var composition = await Composition.CreateAsync();
        const int count = 16;

        var measuredThreadId = Environment.CurrentManagedThreadId;
        var parse = new WalkCounter();
        var revalidate = new WalkCounter();
        var viewRewrite = new WalkCounter();
        PacketPathProbe.ParseWalk = parse.Bump;
        PacketPathProbe.RevalidateWalk = revalidate.Bump;
        PacketPathProbe.ViewRewrite = viewRewrite.Bump;
        try
        {
            for (var index = 0; index < count; index++) await composition.DriveReverseAsync();
        }
        finally
        {
            PacketPathProbe.ParseWalk = null;
            PacketPathProbe.RevalidateWalk = null;
            PacketPathProbe.ViewRewrite = null;
        }

        Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
        Assert.Equal(count, parse.Count);
        Assert.Equal(0, revalidate.Count);
        Assert.Equal(count, viewRewrite.Count);
    }

    /// <summary>
    /// The constructive half of the defaulted-layout contract: every flow packet the real processor
    /// hands the dispatcher carries a layout a successful parse stamped, so production never presents
    /// the invalid shape the layout consumers refuse. (The non-flow arm carries no layout by design —
    /// no consumer on that path reads one.)
    /// </summary>
    [Fact]
    public async Task EveryDispatchedFlowPacketCarriesAParsedLayout()
    {
        await using var composition = await Composition.CreateAsync();

        await composition.DriveForwardMidFlowAsync();
        await composition.DriveReverseAsync();

        Assert.NotEmpty(composition.LaidOutPackets);
        Assert.All(composition.LaidOutPackets, layout => Assert.True(layout.IsValid, "a dispatched flow packet carried a layout no parse produced"));
    }

    /// <summary>
    /// The sequence-gate removal, driven on both legs: the association carries no reference-typed
    /// instance field (a gate cannot exist without one), so a redirected packet pair takes zero gate
    /// entries — and the drive is not vacuous, because both trackers advanced. The red-before count
    /// was two entries for the same drive, one per leg.
    /// </summary>
    [Fact]
    public async Task RedirectPacketTakesZeroSequenceGateEntries()
    {
        await using var composition = await Composition.CreateAsync();
        var association = composition.Association;

        Assert.DoesNotContain(
            typeof(TcpRedirectAssociation).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
            field => !field.FieldType.IsValueType);

        await composition.DriveForwardMidFlowAsync();
        await composition.DriveReverseAsync();

        Assert.NotNull(association.ClientNextSeq);
        Assert.NotNull(association.ServerNextSeq);
    }

    [Fact]
    public async Task WorkCountsOnTheDrivingThreadOnly()
    {
        await using var composition = await Composition.CreateAsync();
        const int count = 8;
        var frame = FrameBuilders.BuildIPv4TcpSyn(s_clientIPv4, s_destIPv4, 51000, 443);

        // The sibling parse happens before the probe is attached: awaiting its completion may
        // resume this test on another pool thread, and a probe attached before that await would
        // belong to the abandoned thread. The parse result travels back through the completion
        // source, so no thread touches a captured local.
        // A completion source rather than a reset event: the sibling thread is the only holder, and a
        // source owns no handle to dispose while the test's failure paths may still have it parked.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            release.Task.GetAwaiter().GetResult();
            var parsed = false;
            for (var index = 0; index < 64; index++) parsed |= IPTcpUdpPacket.TryParse(frame, out _);
            siblingDone.SetResult(parsed);
        });
        thread.Start();
        release.TrySetResult();
        bool siblingParsed;
        try
        {
            siblingParsed = await siblingDone.Task;
        }
        finally
        {
            // Join before letting the test end: the only thread that captured the release latch is done.
            thread.Join();
        }

        var measuredThreadId = Environment.CurrentManagedThreadId;
        var parse = new WalkCounter();
        PacketPathProbe.ParseWalk = parse.Bump;
        try
        {
            for (var index = 0; index < count; index++) await composition.DriveForwardMidFlowAsync();
        }
        finally
        {
            PacketPathProbe.ParseWalk = null;
        }

        Assert.True(siblingParsed, "the sibling thread never parsed a frame");
        Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
        Assert.Equal(count, parse.Count);
    }

    private sealed class WalkCounter
    {
        public int Count { get; private set; }

        public void Bump() => Count++;
    }

    /// <summary>
    /// A pass-through executor that records the layout of every packet the dispatcher completes into
    /// it, so a fact can observe what the real processor handed the flow path without a probe.
    /// </summary>
    private sealed class LayoutRecordingExecutor(IPacketActionExecutor inner) : IPacketActionExecutor
    {
        public List<PacketLayout> Layouts { get; } = [];

        public ValueTask PassAsync(CapturedFlowPacket packet)
        {
            Layouts.Add(packet.Layout);
            return inner.PassAsync(packet);
        }

        public ValueTask BlockAsync(CapturedFlowPacket packet)
        {
            Layouts.Add(packet.Layout);
            return inner.BlockAsync(packet);
        }

        public ValueTask ProxyAsync(CapturedFlowPacket packet, ProxyTarget target, CancellationToken cancellationToken)
        {
            Layouts.Add(packet.Layout);
            return inner.ProxyAsync(packet, target, cancellationToken);
        }
    }

    /// <summary>
    /// The production-shaped composition the walk counts are taken through: real processor,
    /// dispatcher with a Proxy policy that resolves inline to a known server, and the real
    /// <see cref="TcpProxyCoordinator"/> wired as both the reverse handler and the executor's
    /// proxy target, over a pump-owned <see cref="NdisPacketBuffer"/>.
    /// </summary>
    private sealed class Composition : IAsyncDisposable
    {
        private readonly NdisPacketBuffer _forwardBuffer = new();
        private readonly NdisPacketBuffer _reverseBuffer = new();
        private readonly byte[] _forwardFrame;
        private readonly byte[] _reverseFrame;
        private readonly LayoutRecordingExecutor _executor;
        private readonly WindowsAdapter _adapter = new("id-a", "Ethernet", "internal-a", AdapterHandle, 1);

        private ushort AdapterSlot => FlowBuilders.SlotOf(_adapter.StableId, _adapter.Generation);

        private Composition(TcpProxyCoordinator coordinator, TcpRedirectTable table, CapturePacketProcessor processor, LayoutRecordingExecutor executor, byte[] forwardFrame, byte[] reverseFrame)
        {
            Coordinator = coordinator;
            Table = table;
            Processor = processor;
            _executor = executor;
            _forwardFrame = forwardFrame;
            _reverseFrame = reverseFrame;
        }

        private TcpProxyCoordinator Coordinator { get; }

        private TcpRedirectTable Table { get; }

        private CapturePacketProcessor Processor { get; }

        /// <summary>The layouts of the packets the dispatcher handed the executor, in dispatch order.</summary>
        public IReadOnlyList<PacketLayout> LaidOutPackets => _executor.Layouts;

        public TcpRedirectAssociation Association =>
            Table.TryResolveByReverse(Endpoint.From(s_clientIPv4, ListenerPort), Endpoint.From(s_destIPv4, 53000), DateTimeOffset.UtcNow, out var association) && association is not null
                ? association
                : throw new InvalidOperationException("the harness has no reverse-resolvable association");

        private ushort ListenerPort { get; set; }

        public static async Task<Composition> CreateAsync()
        {
            var injector = new FakeInjector();
            var table = new TcpRedirectTable();
            var listenerFactory = new FakeListenerFactory();
            var coordinator = CreateCoordinator(listenerFactory, new FakeRelayFactory(), injector, table, new SelfTrafficRegistry(), new FakeLocalAddressProvider());
            var executor = new NdisPacketActionExecutor(new CountingReinjector(), tcpProxy: coordinator);
            var recorder = new LayoutRecordingExecutor(executor);
            var servers = new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase) { [s_server.Name] = ProxyTarget.FromServer(s_server) };
            var rules = new[] { new PolicyRule(new RuleMatcher(), new FlowDecision(FlowAction.Proxy, 0, s_server.Name)) };
            var configuration = new ValidatedConfiguration(servers, new PolicySnapshot(rules, FlowAction.Block));
            var dispatcher = new FlowDispatcher(configuration, new SelfTrafficRegistry(), recorder, reverseHandler: coordinator);
            var processor = new CapturePacketProcessor(dispatcher, FlowBuilders.Slots);
            var composition = new Composition(
                coordinator,
                table,
                processor,
                recorder,
                FrameBuilders.BuildIPv4TcpFrame(s_clientIPv4, s_destIPv4, 53000, 443, TcpFlagAck, payload: [1, 2, 3, 4]),
                FrameBuilders.BuildIPv4TcpFrame(s_clientIPv4, s_destIPv4, 40001, 53000, 0x12, payload: [1, 2, 3, 4]));

            var syn = FrameBuilders.BuildIPv4TcpSyn(s_clientIPv4, s_destIPv4, 53000, 443);
            using (var synBuffer = new NdisPacketBuffer())
            {
                synBuffer.SetFrame(syn, NdisApiAbi.PacketFlagOnSend, AdapterHandle);
                await composition.Processor.ProcessAsync(NdisCapturedPacket.FromCapture(synBuffer, AdapterHandle), composition._adapter, composition.AdapterSlot, CancellationToken.None);
            }

            await coordinator.DrainPendingSetupsAsync();
            var listener = Assert.Single(listenerFactory.Listeners);
            composition.ListenerPort = listener.TranslatedTuple.Port;
            // The reverse leg's source tuple is the client address on the listener port the
            // factory actually allocated, so the frame must follow the resolved port.
            composition._reverseFrame[34] = (byte)(listener.TranslatedTuple.Port >> 8);
            composition._reverseFrame[35] = (byte)listener.TranslatedTuple.Port;
            return composition;
        }

        public async ValueTask DriveForwardMidFlowAsync()
        {
            _forwardBuffer.SetFrame(_forwardFrame, NdisApiAbi.PacketFlagOnSend, AdapterHandle);
            await Processor.ProcessAsync(NdisCapturedPacket.FromCapture(_forwardBuffer, AdapterHandle), _adapter, AdapterSlot, CancellationToken.None);
        }

        public async ValueTask DriveReverseAsync()
        {
            _reverseBuffer.SetFrame(_reverseFrame, NdisApiAbi.PacketFlagOnReceive, AdapterHandle);
            await Processor.ProcessAsync(NdisCapturedPacket.FromCapture(_reverseBuffer, AdapterHandle), _adapter, AdapterSlot, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            _forwardBuffer.Dispose();
            _reverseBuffer.Dispose();
        }
    }
}
