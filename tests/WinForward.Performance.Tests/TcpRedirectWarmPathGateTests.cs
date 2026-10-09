using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.TcpCoordinatorFakes;

namespace WinForward.Performance.Tests;

public sealed class TcpRedirectWarmPathGateTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);
    private static readonly IPAddress s_clientIPv4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destIPv4 = IPAddress.Parse("192.0.2.53");

    [Fact]
    public async Task TcpRedirectWarmPacketTakesZeroGateEntriesAndZeroClockReads()
    {
        var harness = await RedirectHarness.EstablishAsync();
        await using (harness.Coordinator)
        {
            harness.Table.GateHoldProbe = static () => { };
            var gateEntriesAtStart = harness.Table.GateEntryCountForDiagnostics;
            var reverseProbesAtStart = harness.Table.ReverseProbeCountForDiagnostics;
            harness.Time.ThrowOnRead = true;
            var readsBefore = harness.Time.Reads;

            var forward = MakeForwardTcpPacket(s_clientIPv4, s_destIPv4, 53000, 443, TcpFlagAck);
            Assert.Equal(TcpRedirectOutcome.Injected, await harness.Coordinator.HandlePacketAsync(forward, s_server, CancellationToken.None));

            Assert.Equal(gateEntriesAtStart, harness.Table.GateEntryCountForDiagnostics);
            Assert.Equal(readsBefore, harness.Time.Reads);
            Assert.Equal(0, harness.Table.ReverseProbeCountForDiagnostics - reverseProbesAtStart);

            var reverse = MakeReversePacketClassifierOrientation(s_clientIPv4, harness.ListenerPort, s_destIPv4, 53000, mutateFrame: frame => frame[47] = 0x12);
            Assert.Equal(TcpRedirectOutcome.Injected, await harness.Coordinator.HandlePacketAsync(reverse, s_server, CancellationToken.None));

            Assert.Equal(gateEntriesAtStart, harness.Table.GateEntryCountForDiagnostics);
            Assert.Equal(readsBefore, harness.Time.Reads);
            Assert.Equal(1, harness.Table.ReverseProbeCountForDiagnostics - reverseProbesAtStart);
            harness.Time.ThrowOnRead = false;
        }
    }

    [Fact]
    public async Task ReverseResolveCompletesWhileRedirectGateIsHeld()
    {
        var table = new TcpRedirectTable();
        var now = ActivityBucket.ToUtc(ActivityBucket.FromUtc(DateTimeOffset.UtcNow));
        var key = FlowKey.Create(Endpoint.From(s_clientIPv4, 53000), Endpoint.From(s_destIPv4, 443), TransportProtocol.Tcp, FlowOriginKind.Host);
        Assert.True(table.TryClaim(key, key.Remote, 0x1234, Endpoint.From(IPAddress.Loopback, 42000), forwardLocalAddress: null, now, out var association));

        var parked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        table.GateHoldProbe = () =>
        {
            parked.TrySetResult(true);
            release.Task.Wait(TimeSpan.FromSeconds(10));
        };

        var holderCount = -1;
        var holder = new Thread(() => holderCount = table.Count)
        {
            IsBackground = true,
            Name = "redirect-table-gate-holder",
        };

        holder.Start();
        try
        {
            Assert.True(await parked.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System), "no thread parked inside the redirect-table gate");

            var resolved = false;
            var resolver = new Thread(() => resolved = table.TryResolveByReverse(association!.ReverseSourceEndpoint, association.ReverseDestinationEndpoint, now, out _))
            {
                IsBackground = true,
                Name = "redirect-warm-resolve-during-gate-hold",
            };
            resolver.Start();
            Assert.True(resolver.Join(TimeSpan.FromSeconds(10)), "the warm reverse resolve queued behind the parked redirect-table gate");
            Assert.True(resolved);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.True(holder.Join(TimeSpan.FromSeconds(10)));
        Assert.True(holderCount >= 0);
    }

    private sealed record RedirectHarness(TcpProxyCoordinator Coordinator, TcpRedirectTable Table, CountingTimeProvider Time, ushort ListenerPort)
    {
        internal static async Task<RedirectHarness> EstablishAsync()
        {
            var listenerFactory = new FakeListenerFactory();
            var table = new TcpRedirectTable();
            var time = new CountingTimeProvider(DateTimeOffset.UnixEpoch);
            var activityClock = new ActivityBucketClock(time);
            activityClock.Tick();
            var coordinator = CreateCoordinator(
                listenerFactory,
                new FakeRelayFactory(),
                new FakeInjector(),
                table,
                new SelfTrafficRegistry(),
                new FakeLocalAddressProvider(),
                new TcpRedirectOptions { TimeProvider = time, ActivityClock = activityClock });
            await HandleSynSettledAsync(coordinator, MakeSynPacket(s_clientIPv4, s_destIPv4, 53000, 443), s_server);
            return new RedirectHarness(coordinator, table, time, Assert.Single(listenerFactory.Listeners).TranslatedTuple.Port);
        }
    }
}
