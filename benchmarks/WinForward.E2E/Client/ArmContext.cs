using System.Net;
using System.Net.Sockets;
using WinForward.E2E.Cli;
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

    internal IPEndPoint TcpEndPoint => new(TargetAddress, Options.TcpPort);

    internal IPEndPoint UdpEndPoint => new(TargetAddress, Options.UdpPort);

    internal IPEndPoint DnsEndPoint => new(TargetAddress, Options.DnsPort);

    internal Socket CreateTcpSocket() => new(TargetAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

    internal Socket CreateUdpSocket() => new(TargetAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

    internal long DeadlineTicks(long startTicks) => startTicks + Clock.FromSeconds(Spec.Seconds);

    internal CancellationTokenSource CreateLinkedTokenSource() => CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);

    internal ArmContext WithSpec(ArmSpec spec) => new()
    {
        Spec = spec,
        Options = Options,
        TargetAddress = TargetAddress,
        Sink = Sink,
        Latency = Latency,
        CancellationToken = CancellationToken,
    };
}
