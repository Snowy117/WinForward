using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BenchmarkDotNet.Attributes;
using WinForward.Benchmarks.Stability;
using WinForward.Configuration;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Perf;

/// <summary>
/// Cold-path UDP session establishment through the real SOCKS5 dial path (TCP control
/// connection, UDP ASSOCIATE, relay socket bind, self-traffic registration) against a loopback
/// fake SOCKS5 server. Each invocation populates <see cref="Sessions"/> distinct flows and then
/// waits until every session's triggering datagram has been flushed through its relay — observed
/// via the fake relay's forwarded counter — so the measurement covers full session
/// establishment, not merely enqueueing the fire-and-forget setups.
/// </summary>
[MemoryDiagnoser]
public class UdpSessionBenchmarks
{
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(60);
    private static readonly byte[] Payload = [1];

    [Params(1, 100, 1000)]
    public int Sessions { get; set; }

    private LoopbackSocks5UdpServer _server = null!;
    private Socket _echoDiscard = null!;
    private Socks5Server _socks = null!;

    [GlobalSetup]
    public void Setup()
    {
        // A bound discard socket as the relay's forward destination: forwarded datagrams are
        // absorbed by the kernel instead of generating ICMP noise, and the server's forwarded
        // counter (incremented before the send) still observes every flushed datagram.
        _echoDiscard = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _echoDiscard.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _server = new LoopbackSocks5UdpServer((IPEndPoint)_echoDiscard.LocalEndPoint!);
        _socks = new Socks5Server("benchmark", "127.0.0.1", checked((ushort)_server.ControlEndpoint.Port), null, null);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _server.DisposeAsync().ConfigureAwait(false);
        _echoDiscard.Dispose();
    }

    [Benchmark]
    public async Task PopulateSessionsAsync()
    {
        await using var coordinator = new UdpProxyCoordinator(new Socks5UdpTransportFactory(new SelfTrafficRegistry()), NoopUdpResponseSink.Instance, Sessions);
        var forwardedBaseline = _server.RelayForwarded;
        for (var index = 0; index < Sessions; index++)
        {
            if (!await coordinator.TrySendAsync(BenchmarkShared.CreateFlowKey(index), _socks, Payload, CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Unable to populate the UDP session benchmark.");
            }
        }

        await AwaitSessionsReadyAsync(forwardedBaseline + Sessions).ConfigureAwait(false);
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
        await using var coordinator = new UdpProxyCoordinator(factory, NoopUdpResponseSink.Instance, Sessions);
        var sendsBaseline = factory.Sends;
        for (var index = 0; index < Sessions; index++)
        {
            if (!await coordinator.TrySendAsync(BenchmarkShared.CreateFlowKey(index), _socks, Payload, CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Unable to populate the UDP session benchmark.");
            }
        }

        var stopwatch = Stopwatch.StartNew();
        while (factory.Sends < sendsBaseline + Sessions)
        {
            if (stopwatch.Elapsed > ReadinessTimeout)
            {
                throw new InvalidOperationException($"UDP session setup did not settle within {ReadinessTimeout.TotalSeconds:0}s ({factory.Sends - sendsBaseline}/{Sessions} flushed).");
            }

            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until the fake relay has forwarded one datagram per session (the setup-queue flush
    /// each session performs as its last setup step, which also flips its slot to ready). Spins
    /// for the first few milliseconds so single-session numbers keep microsecond granularity,
    /// then falls back to timed polling for the long 1000-session tails.
    /// </summary>
    private async Task AwaitSessionsReadyAsync(long targetForwarded)
    {
        var stopwatch = Stopwatch.StartNew();
        var spins = 0;
        while (_server.RelayForwarded < targetForwarded)
        {
            if (stopwatch.Elapsed > ReadinessTimeout)
            {
                throw new InvalidOperationException($"UDP session setup did not settle within {ReadinessTimeout.TotalSeconds:0}s ({_server.RelayForwarded - (targetForwarded - Sessions)}/{Sessions} forwarded).");
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
}
