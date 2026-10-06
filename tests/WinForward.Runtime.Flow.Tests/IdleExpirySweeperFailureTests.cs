using System.Globalization;
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
/// S6d: a sweep failure is surfaced as a rate-limited warn while the swallow-and-retry contract
/// holds — the loop keeps sweeping across failures, and repeated failures inside the 5 s window
/// stay silent.
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

        // The idle session makes every sweep tick fail; several ticks prove the loop survives the
        // failures, while the 5 s window admits exactly one warn.
        await WaitForAsync(() => Volatile.Read(ref sweepFailures) >= 4);
        await Task.Delay(250);

        var (_, message) = Assert.Single(logger.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("Idle-expiry sweep failed", StringComparison.Ordinal));
        Assert.Contains("IOException", message, StringComparison.Ordinal);
        Assert.Contains("synthetic sweep failure", message, StringComparison.Ordinal);
        Assert.True(Volatile.Read(ref sweepFailures) >= 5, string.Create(CultureInfo.InvariantCulture, $"The sweeper must keep sweeping across failures (observed {Volatile.Read(ref sweepFailures)} failing ticks)."));

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
        // Regression: before the migration a second dispose called CancelAsync on the already-disposed
        // CTS and threw ObjectDisposedException; it must now join the same drain.
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
