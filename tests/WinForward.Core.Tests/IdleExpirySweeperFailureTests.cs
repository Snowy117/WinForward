using System.Net;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;

namespace WinForward.Core.Tests;

/// <summary>
/// S6d: a sweep failure is surfaced as a rate-limited warn while the swallow-and-retry contract
/// holds — the loop keeps sweeping across failures, and repeated failures inside the 5 s window
/// stay silent.
/// </summary>
public sealed class IdleExpirySweeperFailureTests
{
    private static readonly Socks5Server s_server = new("test", "127.0.0.1", 1080, null, null);

    [Fact]
    public async Task SweepFailureLogsRateLimitedWarnAndKeepsSweeping()
    {
        var logger = new RecordingRuntimeLogger();
        var transportFactory = new ParkedTransportFactory();
        var sweepFailures = 0;
        var coordinator = new UdpProxyCoordinator(
            transportFactory,
            new NoopResponseSink(),
            capacity: 16,
            TimeProvider.System,
            beforeExpiryRecheck: () =>
            {
                Interlocked.Increment(ref sweepFailures);
                return ValueTask.FromException(new IOException("synthetic sweep failure"));
            },
            logger: logger);
        var config = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass));
        var dispatcher = new FlowDispatcher(config, new FakeGuard(), new FakeExecutor());
        var flow = FlowKey.Create(Endpoint.From(IPAddress.Parse("192.0.2.10"), 53000), Endpoint.From(IPAddress.Parse("192.0.2.53"), 53), TransportProtocol.Udp, FlowOriginKind.Host);

        Assert.True(await coordinator.TrySendAsync(flow, s_server, new byte[] { 1 }, CancellationToken.None));
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

        var warn = Assert.Single(logger.Lines, line => line.Level == RuntimeLogLevel.Warn && line.Message.Contains("Idle-expiry sweep failed", StringComparison.Ordinal));
        Assert.Contains("IOException", warn.Message, StringComparison.Ordinal);
        Assert.Contains("synthetic sweep failure", warn.Message, StringComparison.Ordinal);
        Assert.True(Volatile.Read(ref sweepFailures) >= 5, $"The sweeper must keep sweeping across failures (observed {Volatile.Read(ref sweepFailures)} failing ticks).");

        await coordinator.DisposeAsync();
    }

    /// <summary>A transport whose receive never completes: the session stays alive until disposal faults it.</summary>
    private sealed class ParkedTransportFactory : IUdpProxyTransportFactory
    {
        public ParkedTransport? Transport { get; private set; }

        public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
        {
            var transport = new ParkedTransport();
            Transport = transport;
            return ValueTask.FromResult<IUdpProxyTransport>(transport);
        }
    }

    private sealed class ParkedTransport : IUdpProxyTransport
    {
        private readonly TaskCompletionSource<Socks5UdpReceiveResult> _parkedReceive = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPEndPoint RelayEndpoint { get; } = new(IPAddress.Loopback, 50000);
        public IPEndPoint LocalEndpoint { get; } = new(IPAddress.Loopback, 40010);

        public ValueTask SendAsync(IPEndPoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<Socks5UdpReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) => new(_parkedReceive.Task);

        public ValueTask DisposeAsync()
        {
            _parkedReceive.TrySetException(new ObjectDisposedException(nameof(ParkedTransport)));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopResponseSink : IUdpResponseSink
    {
        public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, byte[]? clientMac, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
