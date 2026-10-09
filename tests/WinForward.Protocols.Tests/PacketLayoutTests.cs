using System.Net;
using System.Runtime.CompilerServices;
using WinForward.Core;
using WinForward.Runtime.TcpRedirect;
using Xunit;
using static WinForward.TestSupport.FrameBuilders;
using static WinForward.TestSupport.TcpCoordinatorFakes;

namespace WinForward.Protocols.Tests;

/// <summary>
/// The parse-once contract: the layout a successful parse derives is the one the SYN test, the
/// sequence observations and the endpoint rewriter consume, and the span-taking entry points remain
/// the independent oracle they are compared against. The truncation corpus drives every prefix of
/// an IPv4 and an IPv6 TCP frame, so a layout-driven read can never run past a frame bound or take a
/// different accept/reject decision than the oracle.
/// </summary>
public sealed class PacketLayoutTests
{
    private static readonly IPAddress s_clientIPv4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destIPv4 = IPAddress.Parse("192.0.2.53");
    private static readonly IPAddress s_clientIPv6 = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress s_destIPv6 = IPAddress.Parse("2001:db8::53");
    private static readonly IPAddress s_otherIPv4 = IPAddress.Parse("198.51.100.7");
    private static readonly IPAddress s_otherIPv6 = IPAddress.Parse("2001:db8:1::7");

    [Fact]
    public void PacketLayoutFitsSixteenBytes() => Assert.Equal(16, Unsafe.SizeOf<PacketLayout>());

    /// <summary>
    /// The validity encoding: only a parsed frame yields a layout the consumers accept, and the
    /// defaulted one — what a packet that never parsed carries — is not it. A UDP frame yields a
    /// valid layout that is still not a TCP one, so the two gates stay distinct.
    /// </summary>
    [Fact]
    public void OnlyAParsedFrameYieldsAValidLayout()
    {
        Assert.False(default(PacketLayout).IsValid);
        Assert.False(default(PacketLayout).IsTcp);

        Assert.True(IPTcpUdpPacket.TryParse(BuildIPv4TcpFrame(s_clientIPv4, s_destIPv4, 53_000, 443, TcpFlagSyn), out var tcpView));
        var tcp = PacketLayout.From(tcpView);
        Assert.True(tcp.IsValid);
        Assert.True(tcp.IsTcp);

        Assert.True(IPTcpUdpPacket.TryParse(BuildIPv4UdpFrame(s_clientIPv4, s_destIPv4, 53_000, 53, [1, 2, 3, 4]), out var udpView));
        var udp = PacketLayout.From(udpView);
        Assert.True(udp.IsValid);
        Assert.False(udp.IsTcp);
    }

    [Fact]
    public void LayoutSynTestMatchesTheSpanTest()
    {
        var cases = new (byte[] Frame, bool Expected)[]
        {
            (BuildIPv4TcpFrame(s_clientIPv4, s_destIPv4, 53_000, 443, TcpFlagSyn), true),
            (BuildIPv4TcpFrame(s_clientIPv4, s_destIPv4, 53_000, 443, 0x12), false),
            (BuildIPv4TcpFrame(s_clientIPv4, s_destIPv4, 53_000, 443, 0x10, payload: [1, 2, 3, 4]), false),
            (BuildIPv6TcpFrame(s_clientIPv6, s_destIPv6, 53_000, 443, tcpFlags: TcpFlagSyn), true),
            (BuildIPv6TcpFrame(s_clientIPv6, s_destIPv6, 53_000, 443, tcpFlags: 0x12), false),
            (BuildIPv4UdpFrame(s_clientIPv4, s_destIPv4, 53_000, 53, [1, 2, 3, 4]), false),
            (BuildIPv6TcpFrame(s_clientIPv6, s_destIPv6, 53_000, 443, tcpFlags: TcpFlagSyn)[..40], false),
        };

        foreach (var (frame, expected) in cases)
        {
            Assert.Equal(expected, TcpFrameRewriter.IsTcpSyn(frame));
            if (!IPTcpUdpPacket.TryParse(frame, out var view))
            {
                Assert.False(expected);
                continue;
            }

            Assert.Equal(expected, TcpFrameRewriter.IsTcpSyn(PacketLayout.From(view)));
        }
    }

    [Fact]
    public void ViewDrivenRewriterRejectsWhatTheSpanRewriterRejects()
    {
        foreach (var (pristine, client, destination) in new[]
        {
            (BuildIPv4TcpFrame(s_clientIPv4, s_destIPv4, 53_000, 443, TcpFlagSyn, payload: new byte[64]), s_otherIPv4, s_destIPv4),
            (BuildIPv6TcpFrame(s_clientIPv6, s_destIPv6, 53_000, 443, payload: new byte[64], tcpFlags: TcpFlagSyn), s_otherIPv6, s_destIPv6),
        })
        {
            for (var length = 0; length <= pristine.Length; length++)
            {
                var truncated = pristine[..length];
                var spanFrame = truncated.ToArray();
                var layoutFrame = truncated.ToArray();
                var spanAccepted = PacketChecksums.TryRewriteTcpEndpoints(spanFrame, IPAddressValue.From(client), 53_000, IPAddressValue.From(destination), 443);
                var parses = IPTcpUdpPacket.TryParse(truncated, out var view);
                if (!parses)
                {
                    Assert.False(spanAccepted);
                    continue;
                }

                var layout = PacketLayout.From(view);
                var layoutAccepted = PacketChecksums.TryRewriteTcpEndpoints(layoutFrame, layout, IPAddressValue.From(client), 53_000, IPAddressValue.From(destination), 443);
                Assert.Equal(spanAccepted, layoutAccepted);
                if (!layoutAccepted)
                {
                    // Reject-without-mutating is the primitive's contract; a truncation that both
                    // entry points refuse must leave both frames byte-identical to the input.
                    Assert.Equal(truncated, spanFrame);
                    Assert.Equal(truncated, layoutFrame);
                    continue;
                }

                Assert.Equal(spanFrame, layoutFrame);
                // The independent full-recompute oracle: the layout path is an incremental update,
                // and the two must agree byte-for-byte on a frame with valid input checksums.
                var oracleFrame = truncated.ToArray();
                Assert.True(PacketChecksums.TryRewriteTcpEndpointsFullRecompute(oracleFrame, IPAddressValue.From(client), 53_000, IPAddressValue.From(destination), 443));
                Assert.Equal(oracleFrame, layoutFrame);
            }

            // Cross-family: the argument addresses select the write geometry, so the layout path
            // must reject them and leave the frame untouched.
            var crossFamily = pristine.ToArray();
            var before = crossFamily.ToArray();
            Assert.True(IPTcpUdpPacket.TryParse(crossFamily, out var crossView));
            var crossLayout = PacketLayout.From(crossView);
            var crossSource = crossLayout.Family == (byte)AddressFamilyKind.IPv4 ? s_otherIPv6 : s_otherIPv4;
            Assert.False(PacketChecksums.TryRewriteTcpEndpoints(crossFamily, crossLayout, IPAddressValue.From(crossSource), 53_000, IPAddressValue.From(destination), 443));
            Assert.Equal(before, crossFamily);
        }
    }

    /// <summary>
    /// The defaulted-layout hazard: <see cref="PacketTransport.Tcp"/> is <c>0</c>, so a zeroed layout
    /// reads as a valid TCP layout whose transport header sits at the IP header's own offset and
    /// whose family is IPv4. A packet that never parsed carries exactly that value, so every
    /// layout-driven consumer must refuse it instead of applying the parse's geometry to a frame the
    /// parse never saw. The span entry point is the oracle for both halves: the same frame and the
    /// same endpoint pair are writable through it.
    /// </summary>
    [Fact]
    public void DefaultedLayoutIsRefusedByteIdenticallyRatherThanRewritten()
    {
        var frame = BuildIPv4TcpFrame(s_clientIPv4, s_destIPv4, 53_000, 443, TcpFlagSyn, payload: new byte[64]);

        var oracle = frame.ToArray();
        Assert.True(PacketChecksums.TryRewriteTcpEndpoints(oracle, IPAddressValue.From(s_otherIPv4), 53_000, IPAddressValue.From(s_destIPv4), 443));

        var unparsed = frame.ToArray();
        var before = unparsed.ToArray();
        Assert.False(PacketChecksums.TryRewriteTcpEndpoints(unparsed, default, IPAddressValue.From(s_otherIPv4), 53_000, IPAddressValue.From(s_destIPv4), 443));
        Assert.Equal(before, unparsed);
        Assert.False(TcpFrameRewriter.TryRewriteForwardLeg(unparsed, default, Endpoint.From(s_clientIPv4, 53_000), Endpoint.From(s_destIPv4, 443), CreateHostAssociation(Endpoint.From(IPAddress.Loopback, 40_000)), 40_000));
        Assert.Equal(before, unparsed);
    }

    /// <summary>
    /// The same hazard on the pre-rewrite sequence read: with a defaulted layout the read would land
    /// at the IP header's offset and advance the tracker by a length the layout never proved. The
    /// tracker must stay unobserved, while the span entry point (the oracle) still observes the
    /// frame's real sequence.
    /// </summary>
    [Fact]
    public void DefaultedLayoutObservesNoSequence()
    {
        var frame = BuildIPv4TcpFrame(s_clientIPv4, s_destIPv4, 53_000, 443, payload: new byte[16]);

        var oracle = CreateHostAssociation(Endpoint.From(IPAddress.Loopback, 40_000));
        TcpSequenceObservation.TrackClientSequence(frame, oracle);
        Assert.NotNull(oracle.ClientNextSeq);

        var unparsed = CreateHostAssociation(Endpoint.From(IPAddress.Loopback, 40_000));
        TcpSequenceObservation.TrackClientSequence(frame, default, unparsed);
        Assert.Null(unparsed.ClientNextSeq);
        TcpSequenceObservation.TrackServerSequence(frame, default, unparsed);
        Assert.Null(unparsed.ServerNextSeq);
    }

    [Fact]
    public void SequenceAdvanceIgnoresEthernetPadding()
    {
        var frame = BuildIPv4TcpFrame(s_clientIPv4, s_destIPv4, 53_000, 443, payload: new byte[16]);
        var padded = new byte[1514];
        frame.CopyTo(padded, 0);
        Assert.True(padded.Length > frame.Length);
        Assert.True(IPTcpUdpPacket.TryParse(padded, out var view));

        Assert.Equal(AdvanceViaLayout(padded, view), AdvanceViaSpan(padded));
    }

    [Fact]
    public void ExtensionHeaderFramesProduceTheSameAdvance()
    {
        var frame = BuildIPv6TcpFrameWithHopByHop(s_clientIPv6, s_destIPv6, 53_000, 443);
        Assert.True(IPTcpUdpPacket.TryParse(frame, out var view));

        Assert.Equal(AdvanceViaLayout(frame, view), AdvanceViaSpan(frame));
    }

    private static uint AdvanceViaLayout(byte[] frame, PacketView view)
    {
        var association = CreateHostAssociation(Endpoint.From(IPAddress.Loopback, 40_000));
        TcpSequenceObservation.TrackClientSequence(frame, PacketLayout.From(view), association);
        return Assert.IsType<uint>(association.ClientNextSeq);
    }

    private static uint AdvanceViaSpan(byte[] frame)
    {
        var association = CreateHostAssociation(Endpoint.From(IPAddress.Loopback, 40_000));
        TcpSequenceObservation.TrackClientSequence(frame, association);
        return Assert.IsType<uint>(association.ClientNextSeq);
    }
}
