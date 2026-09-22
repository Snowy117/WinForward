using System.Net;
using System.Net.Sockets;
using BenchmarkDotNet.Attributes;
using WinForward.Benchmarks.Stability;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;

namespace WinForward.Benchmarks.Perf;

/// <summary>
/// Framework-side decomposition of the real-transport UDP session probe (task
/// 09-21-session-creation-cost, design §3): every component of
/// <c>Socks5UdpTransport.CreateAsync</c> is measured against the same loopback SOCKS5 server the
/// real-transport probe uses, so the per-session components can be checked against the full
/// create+dispose path and against the real-transport minus Noop probe total. A fake control
/// connection is not expressible (<see cref="Socks5ControlConnection"/> is sealed with a private
/// constructor and the seam's return type is the concrete class), so connect+handshake and UDP
/// ASSOCIATE are one measured sequence against its connect-only prefix instead of a
/// real-versus-fake pair; the relay socket's "fake" side is measured directly (create + production
/// socket options + bind + close). Every variant asserts its own stage actually executed.
/// </summary>
[MemoryDiagnoser]
public class FrameworkSetupBenchmarks
{
    /// <summary>Mirrors <c>Socks5UdpTransport.RelaySocketReceiveBufferSize</c> (private const in the product): the explicit relay receive headroom the transport applies before bind.</summary>
    private const int RelaySocketReceiveBufferSize = 512 * 1024;

    [Params(1, 100, 1000)]
    public int Sessions { get; set; }

    private LoopbackSocks5UdpServer _server = null!;
    private Socket _echoDiscard = null!;
    private Socks5Server _socks = null!;
    private SelfTrafficRegistry _registry = null!;

    [GlobalSetup]
    public void Setup()
    {
        // A bound discard socket as the relay's forward destination, mirroring UdpSessionBenchmarks.
        _echoDiscard = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _echoDiscard.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _server = new LoopbackSocks5UdpServer((IPEndPoint)_echoDiscard.LocalEndPoint!);
        _socks = new Socks5Server("benchmark", "127.0.0.1", checked((ushort)_server.ControlEndpoint.Port), Username: null, Password: null);
        _registry = new SelfTrafficRegistry();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _server.DisposeAsync().ConfigureAwait(false);
        _echoDiscard.Dispose();
    }

    /// <summary>The whole framework path per session: control connect + greeting, UDP ASSOCIATE, relay socket create/bind/options, self-traffic registration, transport construction — then the symmetric disposal (control close, relay socket close, token release).</summary>
    [Benchmark]
    public async Task RealTransport_CreateDisposeAsync()
    {
        for (var index = 0; index < Sessions; index++)
        {
            var transport = await Socks5UdpTransport.CreateAsync(_socks, _registry, CancellationToken.None, createControl: null, socketFactory: null).ConfigureAwait(false);
            Assert(transport.LocalEndpoint.Port > 0, "the relay socket must be bound before the transport is returned");
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Control-connection connect + no-auth greeting handshake + disposal (no ASSOCIATE): the connect-only prefix of the framework path.</summary>
    [Benchmark]
    public async Task Control_ConnectDisposeAsync()
    {
        for (var index = 0; index < Sessions; index++)
        {
            var control = await Socks5ControlConnection.ConnectAsync(_socks, CancellationToken.None).ConfigureAwait(false);
            await control.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Control connect + greeting + UDP ASSOCIATE + disposal: the connect-only prefix plus the associate step and its relay-endpoint reply parsing.</summary>
    [Benchmark]
    public async Task Control_ConnectAssociateDisposeAsync()
    {
        for (var index = 0; index < Sessions; index++)
        {
            var control = await Socks5ControlConnection.ConnectAsync(_socks, CancellationToken.None).ConfigureAwait(false);
            var relay = await control.UdpAssociateAsync(CancellationToken.None).ConfigureAwait(false);
            Assert(relay.Port > 0, "UDP ASSOCIATE must return the server's relay endpoint");
            await control.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The relay socket's own per-session cost with the product's socket options: create, 512 KiB receive buffer, non-blocking mode, wildcard bind, close.</summary>
    [Benchmark]
    public void RelaySocket_CreateBindClose()
    {
        for (var index = 0; index < Sessions; index++)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveBufferSize = RelaySocketReceiveBufferSize,
            };
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            socket.Blocking = false;
            Assert(socket.LocalEndPoint is not null, "the relay socket must be bound");
            socket.Dispose();
        }
    }

    /// <summary>Self-traffic registration and release per session (the UDP relay tuple the transport registers before its first datagram), verified through the registry's own ownership query.</summary>
    [Benchmark]
    public void SelfTraffic_RegisterRelease()
    {
        for (var index = 0; index < Sessions; index++)
        {
            var local = Endpoint.From(IPAddress.Loopback, checked((ushort)(10_000 + (index % 50_000))));
            var remote = Endpoint.From(IPAddress.Loopback, 50_000);
            var key = FlowKey.Create(local, remote, TransportProtocol.Udp, FlowOriginKind.Host);
            var context = new FlowContext(key, ProcessName: null, ProcessPath: null, AdapterId: null, AdapterName: null, remote.Port);
            var token = _registry.Register(new SelfTrafficRegistry.SelfTrafficKey(TransportProtocol.Udp, local, remote));
            Assert(_registry.IsOwned(context), "the registry must own the registered tuple before release");
            token.Dispose();
            Assert(!_registry.IsOwned(context), "the registry must release the tuple after disposal");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
