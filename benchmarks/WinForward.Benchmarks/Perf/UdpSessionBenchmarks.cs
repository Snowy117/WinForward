using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using BenchmarkDotNet.Attributes;
using WinForward.Benchmarks.Stability;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Perf;

/// <summary>
/// Cold-path UDP session establishment through the real SOCKS5 dial path (TCP control
/// connection, UDP ASSOCIATE, relay socket bind, self-traffic registration) against a loopback
/// fake SOCKS5 server. Each invocation populates <see cref="Sessions"/> distinct flows and then
/// waits until every session's triggering datagram has been flushed through its relay — observed
/// via the in-process server's forwarded counter, or via the echoed response under
/// <c>WINFORWARD_BENCH_EXTERNAL_SERVER=1</c>, where the serving side runs out of process (see
/// <see cref="ExternalLoopbackSocks5UdpServer"/>) — so the measurement covers full session
/// establishment, not merely enqueueing the fire-and-forget setups.
/// </summary>
[MemoryDiagnoser]
public class UdpSessionBenchmarks
{
    private static readonly TimeSpan s_readinessTimeout = TimeSpan.FromSeconds(60);
    private static readonly byte[] s_payload = [1];

    [Params(1, 100, 1000)]
    public int Sessions { get; set; }

    private LoopbackSocks5UdpServer? _server;
    private Socket? _echoDiscard;
    private ExternalLoopbackSocks5UdpServer? _externalServer;
    private Socks5Server _socks = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        if (ExternalLoopbackSocks5UdpServer.IsEnabled)
        {
            _externalServer = await ExternalLoopbackSocks5UdpServer.StartAsync(Sessions, TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            // A bound discard socket as the relay's forward destination: forwarded datagrams are
            // absorbed by the kernel instead of generating ICMP noise, and the server's forwarded
            // counter (incremented before the send) still observes every flushed datagram.
            _echoDiscard = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _echoDiscard.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _server = new LoopbackSocks5UdpServer((IPEndPoint)_echoDiscard.LocalEndPoint!);
        }

        var controlPort = _externalServer is null ? _server!.ControlEndpoint.Port : _externalServer.ControlEndpoint.Port;
        _socks = new Socks5Server("benchmark", "127.0.0.1", checked((ushort)controlPort), Username: null, Password: null);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
        if (_externalServer is not null) await _externalServer.DisposeAsync().ConfigureAwait(false);
        _echoDiscard?.Dispose();
    }

    [Benchmark]
    public async Task PopulateSessionsAsync()
    {
        const int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame;
        using var setupQueuePool = new NativeBufferPool(maximumFrameSize);
        using var receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize));
        using var setupExecutor = new SetupExecutor();
        // The in-process shape keeps the singleton no-op sink so its numbers stay byte-identical to
        // the recorded runs; only the out-of-process shape needs a counter, because the child owns
        // the relay's forwarded total and the echoed response is the parent-visible flush proof.
        var countingSink = _externalServer is null ? null : new ResponseCountingSink();
        await using var coordinator = new UdpProxyCoordinator(new Socks5UdpTransportFactory(new SelfTrafficRegistry(), maximumFrameSize), (IUdpResponseSink?)countingSink ?? NoopUdpResponseSink.Instance, setupQueuePool, receiveWindowPool, setupExecutor, new UdpProxyOptions { Capacity = Sessions });
        var forwardedBaseline = _server?.RelayForwarded ?? 0;
        for (var index = 0; index < Sessions; index++)
        {
            if (!await coordinator.TrySendSpanAsync(BenchmarkShared.CreateFlowKey(index), _socks, s_payload, default, CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Unable to populate the UDP session benchmark.");
            }
        }

        if (countingSink is null)
        {
            await AwaitFlushedAsync(() => _server!.RelayForwarded, forwardedBaseline + Sessions, external: null).ConfigureAwait(false);
        }
        else
        {
            await AwaitFlushedAsync(() => countingSink.Count, Sessions, _externalServer).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Bookkeeping-only decomposition probe for <see cref="PopulateSessionsAsync"/>: identical
    /// coordinator path (slot, setup queue, background setup task, session, association) against
    /// in-memory fake transports, so the delta against the real-transport benchmark isolates the
    /// framework socket cost (control connection, UDP ASSOCIATE, relay socket) from the
    /// product-controllable bookkeeping share this probe measures.
    /// </summary>
    [Benchmark]
    public async Task PopulateSessionsNoopTransportAsync()
    {
        var factory = new BenchmarkUdpTransportFactory();
        const int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame;
        using var setupQueuePool = new NativeBufferPool(maximumFrameSize);
        using var receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize));
        using var setupExecutor = new SetupExecutor();
        await using var coordinator = new UdpProxyCoordinator(factory, NoopUdpResponseSink.Instance, setupQueuePool, receiveWindowPool, setupExecutor, new UdpProxyOptions { Capacity = Sessions });
        var sendsBaseline = factory.Sends;
        for (var index = 0; index < Sessions; index++)
        {
            if (!await coordinator.TrySendSpanAsync(BenchmarkShared.CreateFlowKey(index), _socks, s_payload, default, CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Unable to populate the UDP session benchmark.");
            }
        }

        var stopwatch = Stopwatch.StartNew();
        while (factory.Sends < sendsBaseline + Sessions)
        {
            if (stopwatch.Elapsed > s_readinessTimeout)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"UDP session setup did not settle within {s_readinessTimeout.TotalSeconds:0}s ({factory.Sends - sendsBaseline}/{Sessions} flushed)."));
            }

            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until every session's triggering datagram has been flushed through its relay (the
    /// setup-queue flush, which also flips its slot to ready) — the caller supplies the counter: the
    /// in-process server's forwarded total, or the echo count of the out-of-process shape (see
    /// <see cref="ResponseCountingSink"/>). Spins for the first few milliseconds so single-session
    /// numbers keep microsecond granularity, then falls back to timed polling for the long
    /// 1000-session tails, and fails the run as soon as the out-of-process child is gone.
    /// </summary>
    private async Task AwaitFlushedAsync(Func<long> flushedCount, long target, ExternalLoopbackSocks5UdpServer? external)
    {
        var stopwatch = Stopwatch.StartNew();
        var spins = 0;
        while (flushedCount() < target)
        {
            external?.ThrowIfExited();
            if (stopwatch.Elapsed > s_readinessTimeout)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"UDP session setup did not settle within {s_readinessTimeout.TotalSeconds:0}s ({flushedCount() - (target - Sessions)}/{Sessions} sessions flushed through the harness relay)."));
            }

            if (spins < 200_000)
            {
                spins++;
                Thread.SpinWait(20);
            }
            else
            {
                await Task.Delay(1).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The out-of-process probe's flush signal: the child owns the relay's forwarded counter, and a
    /// response can only reach this sink after the child forwarded the datagram, so the count of
    /// echoed responses stands in for it. Unused in the in-process shape (its discard destination
    /// never replies).
    /// </summary>
    private sealed class ResponseCountingSink : IUdpResponseSink
    {
        private long _count;

        public long Count => Interlocked.Read(ref _count);

        public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }
    }
}
