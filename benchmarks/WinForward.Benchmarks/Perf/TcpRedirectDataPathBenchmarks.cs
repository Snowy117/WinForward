using System.Net;
using BenchmarkDotNet.Attributes;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime.TcpRedirect;

namespace WinForward.Benchmarks.Perf;

/// <summary>
/// The composed per-packet cost of the proxy data path (research F1 follow-up / F4): sequence tracking
/// followed by the leg's rewrite, in both association shapes and both address families, at a small and
/// a full-size frame. This is the number the proxy adds to every packet of a proxied connection, and
/// the baseline the parse-once (F4.2) and direction-normalized-key (F3/F4.1) work is measured against.
/// <para>
/// The rows stop at the rewrite: the send itself is a driver IOCTL, so a counting fake would add noise
/// rather than information, and the Windows driver's cost is out of scope for a Linux micro row. What is
/// measured here is exactly the CPU work the pump performs per packet before it hands the buffer on.
/// </para>
/// <para>
/// Setup proves every row would <em>succeed</em> on the pristine frame before any of them is timed: a
/// row that silently measured a rejected rewrite (wrong family, short header) would report a plausible
/// number for the wrong question.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class TcpRedirectDataPathBenchmarks
{
    private const ushort ListenerPort = 1080;

    // ReSharper disable PropertyCanBeMadeInitOnly.Global // BenchmarkDotNet's [Params] contract is a settable public property: it writes each case's value through the setter, a reflection-based writer the analyzer cannot see (the same false-positive family as the [Params] accessor suppression scoped to this directory in .editorconfig).
    [Params(false, true)]
    public bool Ipv6 { get; set; }

    [Params(128, 1400)]
    public int FrameSize { get; set; }
    // ReSharper restore PropertyCanBeMadeInitOnly.Global

    private byte[] _pristine = null!;
    private byte[] _scratch = null!;
    private Endpoint _client;
    private Endpoint _server;
    private TcpRedirectAssociation _host = null!;
    private TcpRedirectAssociation _forwarded = null!;

    [GlobalSetup]
    public void Setup()
    {
        _pristine = Ipv6
            ? BenchmarkShared.CreateIpv6TcpFrame(FrameSize, bareSyn: false)
            : BenchmarkShared.CreateIpv4TcpFrame(FrameSize, bareSyn: false);
        _scratch = new byte[FrameSize];
        _client = Ipv6
            ? Endpoint.From(IPAddress.Parse("2001:db8::10"), 53_000)
            : Endpoint.From(IPAddress.Parse("192.0.2.10"), 53_000);
        _server = Ipv6
            ? Endpoint.From(IPAddress.Parse("2001:db8::80"), 443)
            : Endpoint.From(IPAddress.Parse("192.0.2.80"), 443);

        var hostKey = FlowKey.Create(_client, _server, TransportProtocol.Tcp, FlowOriginKind.Host, new AdapterContext("adapter-0", 0));
        var forwardedKey = FlowKey.Create(_client, _server, TransportProtocol.Tcp, FlowOriginKind.Forwarded, new AdapterContext("adapter-0", 0));
        var translated = Endpoint.From(IPAddress.Parse(Ipv6 ? "2001:db8::1080" : "192.168.77.2"), ListenerPort);
        var now = DateTimeOffset.UnixEpoch;
        _host = new TcpRedirectAssociation(hostKey, _server, 0, translated, forwardLocalAddress: null, 1, now);
        // The forward-local address must be the flow's own family: production takes it from the
        // adapter, and a mismatched family makes the rewriter reject every packet.
        var forwardLocal = IPAddress.Parse(Ipv6 ? "2001:db8::1" : "192.168.77.1");
        _forwarded = new TcpRedirectAssociation(forwardedKey, _server, 0, translated, IPAddressValue.From(forwardLocal), 2, now);

        ProveRowsSucceed();
    }

    [Benchmark]
    public bool ForwardLegHost()
    {
        _pristine.AsSpan().CopyTo(_scratch);
        TcpSequenceObservation.TrackClientSequence(_scratch, _host);
        return TcpFrameRewriter.TryRewriteForwardLeg(_scratch, _client, _server, _host, ListenerPort);
    }

    [Benchmark]
    public bool ForwardLegForwarded()
    {
        _pristine.AsSpan().CopyTo(_scratch);
        TcpSequenceObservation.TrackClientSequence(_scratch, _forwarded);
        return TcpFrameRewriter.TryRewriteForwardLeg(_scratch, _client, _server, _forwarded, ListenerPort);
    }

    [Benchmark]
    public bool ReverseLegHost()
    {
        _pristine.AsSpan().CopyTo(_scratch);
        TcpSequenceObservation.TrackServerSequence(_scratch, _host);
        if (!PacketChecksums.TryRewriteTcpEndpoints(_scratch, _server.Address, _server.Port, _client.Address, ListenerPort)) return false;
        TcpFrameRewriter.SwapEthernetMacs(_scratch);
        return true;
    }

    [Benchmark]
    public bool ReverseLegForwarded()
    {
        _pristine.AsSpan().CopyTo(_scratch);
        TcpSequenceObservation.TrackServerSequence(_scratch, _forwarded);
        return PacketChecksums.TryRewriteTcpEndpoints(_scratch, _server.Address, _server.Port, _client.Address, ListenerPort);
    }

    /// <summary>
    /// Runs each leg's operations once on a pristine copy and fails setup if any of them rejects the
    /// frame, so a measurement of the rejection path is impossible to record by accident.
    /// </summary>
    private void ProveRowsSucceed()
    {
        if (!Rewrite(host: true)) throw new InvalidOperationException("The forward host-shape row rejects the pristine frame.");
        if (!Rewrite(host: false)) throw new InvalidOperationException("The forward forwarded-shape row rejects the pristine frame.");
        if (!Reverse(host: true)) throw new InvalidOperationException("The reverse host-shape row rejects the pristine frame.");
        if (!Reverse(host: false)) throw new InvalidOperationException("The reverse forwarded-shape row rejects the pristine frame.");
    }

    private bool Rewrite(bool host)
    {
        _pristine.AsSpan().CopyTo(_scratch);
        var association = host ? _host : _forwarded;
        TcpSequenceObservation.TrackClientSequence(_scratch, association);
        return TcpFrameRewriter.TryRewriteForwardLeg(_scratch, _client, _server, association, ListenerPort);
    }

    private bool Reverse(bool host)
    {
        _pristine.AsSpan().CopyTo(_scratch);
        TcpSequenceObservation.TrackServerSequence(_scratch, host ? _host : _forwarded);
        if (!PacketChecksums.TryRewriteTcpEndpoints(_scratch, _server.Address, _server.Port, _client.Address, ListenerPort)) return false;
        if (host) TcpFrameRewriter.SwapEthernetMacs(_scratch);
        return true;
    }
}
