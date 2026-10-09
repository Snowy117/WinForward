using System.Net;
using Microsoft.Extensions.Logging;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.UdpProxy;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;

namespace WinForward.Runtime.Flow.Tests;

/// <summary>
/// A sweep failure is surfaced as a rate-limited warn while the swallow-and-retry contract holds:
/// the loop keeps sweeping across failures, and repeated failures inside the 5 s window stay silent.
/// </summary>
public sealed class IdleExpirySweeperFailureTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, Username: null, Password: null);

    [Fact]
    public async Task SweepFailureLogsRateLimitedWarnAndKeepsSweeping()
    {
        var logger = new RecordingLogger();
        var transportFactory = new ParkedTransportFactory();
        var sweepFailures = 0;
        var coordinator = UdpCoordinatorFakes.CreateCoordinator(
            transportFactory,
            new NoopResponseSink(),
            new UdpProxyOptions
            {
                Capacity = 16,
                BeforeExpiryRecheck = () =>
                {
                    // ReSharper disable once AccessToModifiedClosure // sweepFailures is only ever touched through Interlocked/Volatile from the sweeper and the test thread, so the modification the inspection guards against is the checked access itself.
                    Interlocked.Increment(ref sweepFailures);
                    return ValueTask.FromException(new IOException("synthetic sweep failure"));
                },
                Logger = logger,
            });
        var config = new ValidatedConfiguration(
            new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass));
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), new FakeExecutor());
        var flow = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);

        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [1], default, CancellationToken.None));
        await WaitForAsync(() => transportFactory.Transport is not null);

        await using var sweeper = new IdleExpirySweeper(
            dispatcher,
            tcp: null,
            udp: coordinator,
            interval: TimeSpan.FromMilliseconds(50),
            flowIdleTimeout: TimeSpan.FromMinutes(5),
            redirectIdleTimeout: TimeSpan.FromMinutes(5),
            relayIdleTimeout: TimeSpan.FromMilliseconds(50),
            logger: logger);
        sweeper.Start();

        // The idle session makes every sweep tick fail; further failing ticks prove the loop survives
        // the failures, and the wait for them is a poll rather than a sleep-then-hope: a starved
        // continuation can leave a fixed delay short of the ticks it was meant to admit.
        await WaitForAsync(() => Volatile.Read(ref sweepFailures) >= 5);

        var (_, message) = Assert.Single(logger.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("Idle-expiry sweep failed", StringComparison.Ordinal));
        Assert.Contains("IOException", message, StringComparison.Ordinal);
        Assert.Contains("synthetic sweep failure", message, StringComparison.Ordinal);

        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsyncJoinsAnInFlightTick()
    {
        var logger = new RecordingLogger();
        var tickEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportFactory = new ParkedTransportFactory();
        var coordinator = UdpCoordinatorFakes.CreateCoordinator(
            transportFactory,
            new NoopResponseSink(),
            new UdpProxyOptions
            {
                Capacity = 16,
                BeforeExpiryRecheck = () =>
                {
                    tickEntered.TrySetResult();
                    return new ValueTask(release.Task);
                },
                Logger = logger,
            });
        var config = new ValidatedConfiguration(
            new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass));
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), new FakeExecutor());
        var flow = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);

        Assert.True(await coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(s_server), [1], default, CancellationToken.None));
        await WaitForAsync(() => transportFactory.Transport is not null);

        var sweeper = new IdleExpirySweeper(
            dispatcher,
            tcp: null,
            udp: coordinator,
            interval: TimeSpan.FromMilliseconds(50),
            flowIdleTimeout: TimeSpan.FromMinutes(5),
            redirectIdleTimeout: TimeSpan.FromMinutes(5),
            relayIdleTimeout: TimeSpan.FromMilliseconds(50),
            logger: logger);
        sweeper.Start();
        await tickEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var dispose = sweeper.DisposeAsync().AsTask();
        await Task.Delay(50);

        // Regression: if disposal did not join its Run child, it would return while the sweep tick
        // is still awaiting the expiry recheck.
        Assert.False(dispose.IsCompleted);

        release.TrySetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));

        await sweeper.DisposeAsync();
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task SecondDisposeAsyncJoinsInsteadOfThrowing()
    {
        var logger = new RecordingLogger();
        var config = new ValidatedConfiguration(
            new Dictionary<string, ProxyTarget>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass));
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), new FakeExecutor());
        var sweeper = new IdleExpirySweeper(
            dispatcher,
            tcp: null,
            udp: null,
            interval: TimeSpan.FromMilliseconds(50),
            logger: logger);
        sweeper.Start();
        await Task.Delay(80);

        await sweeper.DisposeAsync();
        // A second dispose must join the same drain rather than cancel an already-disposed token
        // source, which would throw ObjectDisposedException out of the caller's teardown path.
        await sweeper.DisposeAsync();
    }

    /// <summary>A transport whose receive never completes: the session stays alive until disposal faults it.</summary>
    private sealed class ParkedTransportFactory : IUdpProxyTransportFactory
    {
        public ParkedTransport? Transport { get; private set; }

        public ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
        {
            var transport = new ParkedTransport();
            Transport = transport;
            return ValueTask.FromResult<IUdpProxyTransport>(transport);
        }
    }

    private sealed class ParkedTransport : IUdpProxyTransport
    {
        private readonly TaskCompletionSource<UdpTransportReceiveResult> _parkedReceive = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPEndPoint PeerEndpoint { get; } = new(IPAddress.Loopback, 50000);
        public IPEndPoint LocalEndpoint { get; } = new(IPAddress.Loopback, 40010);

        public ValueTask SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<UdpTransportReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) => new(_parkedReceive.Task);

        public ValueTask DisposeAsync()
        {
            _parkedReceive.TrySetException(new ObjectDisposedException(nameof(ParkedTransport)));
            return ValueTask.CompletedTask;
        }
    }

}
