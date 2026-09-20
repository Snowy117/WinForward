using System.Net;
using WinForward.Configuration;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.Core.Tests.TcpCoordinatorFakes;

namespace WinForward.Core.Tests;

/// <summary>
/// The registration contract of the redirect setup (R7): a fault between the flow claim and a
/// registered session releases the claimed listener, the table alias, and the self-traffic token
/// exactly once, so a hard fault cannot leave half-registered redirect state behind.
/// </summary>
public sealed class TcpRedirectSetupTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);

    [Fact]
    public async Task RegistrationFaultReleasesTheClaimedListenerAliasAndToken()
    {
        var table = new TcpRedirectTable();
        var selfTraffic = new SelfTrafficRegistry();
        var logger = new RecordingRuntimeLogger();
        var store = new TcpRedirectSessionStore(table, logger, capacity: 8, TimeProvider.System);
        var listenerFactory = new FakeListenerFactory();
        var setup = new TcpRedirectSetup(
            listenerFactory,
            table,
            selfTraffic,
            new FakeLocalAddressProvider(),
            new FakeInjector(),
            logger,
            store,
            new ClientResetInjector(new FakeInjector(), logger, store.TearDownSessionAsync, store.FailAssociationAsync),
            TestPools.SynCopyPool,
            TimeProvider.System);
        // A disposed store's shutdown token is the deterministic stand-in for a hard fault between
        // the claim and a registered session; registration itself cannot be made to fail on demand.
        await store.DisposeAsync();
        var packet = MakeSynPacket(IPAddress.Parse("192.0.2.10"), IPAddress.Parse("192.0.2.53"), 53000, 443);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => setup.SetupNewRedirectAsync(packet, s_server, CancellationToken.None).AsTask());

        Assert.Equal(0, table.Count);
        var listener = Assert.Single(listenerFactory.Listeners);
        Assert.True(listener.IsDisposed);
        Assert.False(selfTraffic.IsOwned(OwnershipContext(listener.TranslatedTuple)));
    }

    private static FlowContext OwnershipContext(Endpoint translatedTuple)
        => new(FlowKey.Create(translatedTuple, translatedTuple, TransportProtocol.Tcp, FlowOriginKind.Host), ProcessName: null, ProcessPath: null, AdapterId: null, AdapterName: null, translatedTuple.Port);
}
