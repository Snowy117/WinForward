#pragma warning disable CA1416 // The relay-factory constants are SupportedOSPlatform(windows) but are plain values; the handshake path itself is platform-neutral managed code.

using System.Net;
using BenchmarkDotNet.Attributes;
using WinForward.Benchmarks.Stability;
using WinForward.Configuration;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.TcpRedirect;

namespace WinForward.Benchmarks.Perf;

/// <summary>
/// The fixed per-connection cost of establishing a SOCKS5 TCP relay: greeting/method
/// negotiation, TCP CONNECT for the destination, and teardown, against a loopback fake SOCKS5
/// server. Attempt caps and per-attempt timeout mirror the production relay call site
/// (<see cref="TcpProxyRelayFactory"/>) so the number transfers to the real redirect path.
/// Baseline-only per the task decision of 2026-08-29: no optimization work rides on this.
/// </summary>
[MemoryDiagnoser]
public class Socks5HandshakeBenchmarks
{
    private static long s_sink;

    private LoopbackSocks5TcpServer _server = null!;
    private Socks5Server _socks = null!;
    private IPEndPoint _destination = null!;

    [GlobalSetup]
    public void Setup()
    {
        _server = new LoopbackSocks5TcpServer();
        _socks = new Socks5Server("benchmark", "127.0.0.1", checked((ushort)_server.Endpoint.Port), null, null);
        _destination = new IPEndPoint(IPAddress.Parse("192.0.2.80"), 443);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public async Task ConnectAndDestinationAsync()
    {
        await using var control = await Socks5ControlConnection.ConnectAsync(
            _socks,
            CancellationToken.None,
            maxAttempts: TcpProxyRelayFactory.RelayConnectMaxAttempts,
            perAttemptTimeout: TcpProxyRelayFactory.RelayConnectAttemptTimeout).ConfigureAwait(false);
        await control.ConnectDestinationAsync(_destination, CancellationToken.None).ConfigureAwait(false);
        Volatile.Write(ref s_sink, _server.ConnectReplies);
    }
}

#pragma warning restore CA1416
