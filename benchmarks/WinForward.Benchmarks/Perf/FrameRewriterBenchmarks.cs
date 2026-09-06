using System.Net;
using BenchmarkDotNet.Attributes;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.TcpRedirect;

namespace WinForward.Benchmarks.Perf;

/// <summary>
/// Per-packet TCP frame rewriting for proxied TCP flows — the only per-packet work the proxy
/// data plane adds over a plain pass. Covers <see cref="TcpFrameRewriter.IsTcpSyn"/> (the
/// SYN gate every captured TCP frame passes), <see cref="TcpFrameRewriter.SwapEthernetMacs"/>
/// (host-shape MAC swap), and <see cref="TcpFrameRewriter.TryRewriteForwardLeg"/> in both
/// association shapes (host IP-swap vs forwarded DNAT).
/// </summary>
[MemoryDiagnoser]
public class FrameRewriterBenchmarks
{
    private const ushort ListenerPort = 1080;

    [Params(128, 1400)]
    public int FrameSize { get; set; }

    private byte[] _synFrame = null!;
    private byte[] _dataFrame = null!;
    private byte[] _scratch = null!;
    private Endpoint _client;
    private Endpoint _server;
    private TcpRedirectAssociation _hostAssociation = null!;
    private TcpRedirectAssociation _forwardedAssociation = null!;

    [GlobalSetup]
    public void Setup()
    {
        _synFrame = BenchmarkShared.CreateIpv4TcpFrame(FrameSize, bareSyn: true);
        _dataFrame = BenchmarkShared.CreateIpv4TcpFrame(FrameSize, bareSyn: false);
        _scratch = new byte[FrameSize];
        _client = Endpoint.From(IPAddress.Parse("192.0.2.10"), 53_000);
        _server = Endpoint.From(IPAddress.Parse("192.0.2.80"), 443);
        var hostKey = BenchmarkShared.CreateFlowKey(0);
        var forwardedKey = FlowKey.Create(_client, _server, TransportProtocol.Tcp, FlowOriginKind.Forwarded, new AdapterContext("adapter-0", null, 0));
        var translated = Endpoint.From(IPAddress.Parse("192.168.77.2"), ListenerPort);
        var originAdapter = new AdapterContext("adapter-0", null, 0);
        var now = DateTimeOffset.UnixEpoch;
        _hostAssociation = new TcpRedirectAssociation(hostKey, _server, originAdapter, 0, translated, null, 1, now);
        _forwardedAssociation = new TcpRedirectAssociation(forwardedKey, _server, originAdapter, 0, translated, IPAddressValue.From(IPAddress.Parse("192.168.77.1")), 2, now);
    }

    [Benchmark]
    public int IsTcpSyn() => TcpFrameRewriter.IsTcpSyn(_synFrame) ? 1 : 0;

    [Benchmark]
    public void SwapEthernetMacs() => TcpFrameRewriter.SwapEthernetMacs(_scratch);

    /// <summary>
    /// The rewrite mutates the frame, so each invocation restores the pristine mid-flow frame
    /// into the scratch buffer first. The span copy (~tens of ns) is part of the measured time;
    /// the swap-only benchmark above isolates the MAC swap without that restore.
    /// </summary>
    [Benchmark]
    public bool TryRewriteForwardLegHostShape()
    {
        _dataFrame.AsSpan().CopyTo(_scratch);
        return TcpFrameRewriter.TryRewriteForwardLeg(_scratch, _client, _server, _hostAssociation, ListenerPort);
    }

    /// <summary>Forwarded DNAT shape; same restore-then-rewrite structure as the host shape.</summary>
    [Benchmark]
    public bool TryRewriteForwardLegForwardedShape()
    {
        _dataFrame.AsSpan().CopyTo(_scratch);
        return TcpFrameRewriter.TryRewriteForwardLeg(_scratch, _client, _server, _forwardedAssociation, ListenerPort);
    }

    /// <summary>
    /// Direct endpoint rewrite (the checksum work P2a isolates): addresses + ports + checksum
    /// update only, no MAC swap or association branching. Same restore-then-rewrite structure.
    /// </summary>
    [Benchmark]
    public bool TryRewriteEndpointsDirect()
    {
        _dataFrame.AsSpan().CopyTo(_scratch);
        return PacketChecksums.TryRewriteTcpEndpoints(_scratch, _server.Address, _server.Port, _client.Address, ListenerPort);
    }
}
