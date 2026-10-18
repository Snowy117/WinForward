using System.Net;
using System.Net.Sockets;
using WinForward.E2E.Cli;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Client;

internal sealed class ArmContext
{
    internal required ArmSpec Spec { get; init; }

    internal required ClientOptions Options { get; init; }

    internal required IPAddress TargetAddress { get; init; }

    internal required JsonlSink Sink { get; init; }

    internal required LatencySet Latency { get; init; }

    internal required CancellationToken CancellationToken { get; init; }

    internal Socks5Proxy? Socks5 { get; init; }

    internal IPEndPoint TcpEndPoint => new(TargetAddress, Options.TcpPort);

    internal IPEndPoint UdpEndPoint => new(TargetAddress, Options.UdpPort);

    internal IPEndPoint DnsEndPoint => new(TargetAddress, Options.DnsPort);

    internal Socket CreateTcpSocket() => new(Socks5?.AddressFamily ?? TargetAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

    internal Socket CreateUdpSocket() => new(TargetAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

    internal ValueTask<LaneOpenResult> TryConnectAsync(Socket socket, EndPoint target, CancellationToken token) =>
        ClientTransport.TryConnectAsync(socket, target, Socks5, token);

    internal long DeadlineTicks(long startTicks) => startTicks + Clock.FromSeconds(Spec.Seconds);

    internal CancellationTokenSource CreateLinkedTokenSource() => CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);

    internal ArmContext WithSpec(ArmSpec spec) => new()
    {
        Spec = spec,
        Options = Options,
        TargetAddress = TargetAddress,
        Sink = Sink,
        Latency = Latency,
        Socks5 = Socks5,
        CancellationToken = CancellationToken,
    };
}
