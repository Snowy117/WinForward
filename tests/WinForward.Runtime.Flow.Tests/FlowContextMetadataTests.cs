using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.TestSupport;
using WinForward.Windows;
using Xunit;

namespace WinForward.Runtime.Flow.Tests;

/// <summary>
/// The slim per-packet context contract: the adapter identity is the slot table's interned metadata
/// (carried on warm packets too, because cold consumers log it from every packet), while the process
/// identity is created once at claim and is never re-resolved on a warm hit.
/// </summary>
public sealed class FlowContextMetadataTests
{
    private static readonly IPAddress s_client = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destination = IPAddress.Parse("192.0.2.53");
    private const nint AdapterHandle = 0x1234;

    /// <summary>
    /// This class owns its adapter's stable ID. The interning table is process-lived and
    /// <see cref="AdapterSlotTable.TryIntern"/> republishes the friendly name on every call, so two
    /// parallel classes that intern the same ID under different names race on what the slot resolves
    /// to — observed as AdapterName "id-a" where this class expects "Ethernet". A shared well-known ID
    /// is only safe for a class that asserts on the slot's identity, never on its metadata.
    /// </summary>
    private const string AdapterStableId = "flow-context-metadata-adapter";

    [Fact]
    public async Task ClaimedFlowCarriesTheInternedAdapterAndProcessMetadata()
    {
        var (dispatcher, executor) = CreateDispatcher();
        var context = Classify();

        Assert.Null(context.Process);
        await dispatcher.DispatchAsync(new CapturedFlowPacket(new PacketLease(new byte[] { 1, 2, 3, 4 }), context), CancellationToken.None);

        var claimed = Assert.Single(executor.Packets);
        Assert.Equal(AdapterStableId, claimed.Context.AdapterId);
        Assert.Equal("Ethernet", claimed.Context.AdapterName);
        Assert.Equal("dns.exe", claimed.Context.ProcessName);
        Assert.NotNull(claimed.Context.Process);
        Assert.Equal((ushort)53, claimed.Context.RemotePort);
    }

    [Fact]
    public async Task WarmHitKeepsTheAdapterMetadataAndCarriesNoProcessMetadata()
    {
        var (dispatcher, executor) = CreateDispatcher();

        await dispatcher.DispatchAsync(new CapturedFlowPacket(new PacketLease(new byte[] { 1, 2, 3, 4 }), Classify()), CancellationToken.None);
        // A warm packet arrives with the classifier's context: the claim-time attribution is not
        // carried onto it and is not re-run, so its process fields stay null exactly as before.
        await dispatcher.DispatchAsync(new CapturedFlowPacket(new PacketLease(new byte[] { 5, 6, 7, 8 }), Classify()), CancellationToken.None);

        Assert.Equal(2, executor.Packets.Count);
        var warm = executor.Packets[1];
        Assert.Equal(AdapterStableId, warm.Context.AdapterId);
        Assert.Equal("Ethernet", warm.Context.AdapterName);
        Assert.Null(warm.Context.Process);
        Assert.Null(warm.Context.ProcessName);
        Assert.Null(warm.Context.ProcessPath);
    }

    private static FlowContext Classify()
    {
        var adapter = new WindowsAdapter(AdapterStableId, "Ethernet", "internal-a", AdapterHandle, 7);
        var view = new PacketView(
            PacketTransport.Udp,
            IPAddressValue.From(s_client),
            IPAddressValue.From(s_destination),
            53000,
            53,
            IPHeaderLength: 20,
            TransportHeaderLength: 8,
            TransportLength: 8,
            TcpFlags: 0);
        return PacketFlowClassifier.ClassifyFlow(view, adapter, isOnSend: true, FlowBuilders.SlotOf(adapter.StableId, adapter.Generation, adapter.FriendlyName), FlowBuilders.Slots);
    }

    private static (FlowDispatcher Dispatcher, RecordingExecutor Executor) CreateDispatcher()
    {
        var server = new Socks5Server("primary", "127.0.0.1", 1080, Username: null, Password: null);
        var servers = new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase) { [server.Name] = ProxyTarget.FromServer(server) };
        var rules = new[]
        {
            new PolicyRule(
                new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns.exe" }),
                new FlowDecision(FlowAction.Proxy, 0, server.Name)),
        };
        var configuration = new ValidatedConfiguration(servers, new PolicySnapshot(rules, FlowAction.Block));
        var executor = new RecordingExecutor();
        return (new FlowDispatcher(configuration, new FakeGuard(), executor, attributor: new FakeAttributor("dns.exe")), executor);
    }

    /// <summary>Records every packet it is handed, so a fact can inspect what a flow's consumers saw.</summary>
    private sealed class RecordingExecutor : IPacketActionExecutor
    {
        public List<CapturedFlowPacket> Packets { get; } = [];

        public ValueTask PassAsync(CapturedFlowPacket packet) => Record(packet);

        public ValueTask BlockAsync(CapturedFlowPacket packet) => Record(packet);

        public ValueTask ProxyAsync(CapturedFlowPacket packet, ProxyTarget target, CancellationToken cancellationToken) => Record(packet);

        private ValueTask Record(CapturedFlowPacket packet)
        {
            Packets.Add(packet);
            return ValueTask.CompletedTask;
        }
    }
}
