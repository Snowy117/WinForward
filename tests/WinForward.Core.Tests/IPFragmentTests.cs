using System.Buffers.Binary;
using System.Net;
using WinForward.Core;
using WinForward.Protocols;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class IPFragmentTests
{
    private static readonly IPAddress s_sourceV4 = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_destinationV4 = IPAddress.Parse("192.0.2.53");
    private static readonly IPAddress s_sourceV6 = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress s_destinationV6 = IPAddress.Parse("2001:db8::53");

    [Fact]
    public void DetectsIpv4MfAndOffsetFragmentsButNotDfOnly()
    {
        // The mask mirrors the canonical parser: reserved bit, MF, and a non-zero offset make a
        // fragment; DF alone does not.
        Assert.True(IPFragment.IsFragment(WithIpv4Flags(0x2000)));
        Assert.True(IPFragment.IsFragment(WithIpv4Flags(0x0001)));
        Assert.True(IPFragment.IsFragment(WithIpv4Flags(0x8000)));
        Assert.False(IPFragment.IsFragment(WithIpv4Flags(0x4000)));
        Assert.False(IPFragment.IsFragment(WithIpv4Flags(0x0000)));
    }

    [Fact]
    public void DetectsIpv6FragmentHeaderAnywhereInChain()
    {
        var direct = TcpFragmentHandlingTests.BuildIpv6Fragment(s_sourceV6, s_destinationV6, 53000, 443);
        Assert.True(IPFragment.IsFragment(direct));

        // Hop-by-hop first, then the fragment header: the chain walk must reach it.
        var withHopByHop = new byte[14 + 40 + 8 + 8 + 20];
        direct.AsSpan(0, 14 + 40).CopyTo(withHopByHop);
        BinaryPrimitives.WriteUInt16BigEndian(withHopByHop.AsSpan(18, 2), 8 + 8 + 20);
        withHopByHop[20] = 0; // nextHeader = Hop-by-Hop
        withHopByHop[54] = 44; // Hop-by-Hop carries the fragment header
        withHopByHop[55] = 0; // 8-byte Hop-by-Hop
        direct.AsSpan(54).CopyTo(withHopByHop.AsSpan(62));
        Assert.True(IPFragment.IsFragment(withHopByHop));

        // A plain extension chain without a fragment header is not a fragment.
        var hopByHopOnly = FrameBuilders.BuildIpv6TcpFrameWithHopByHop(s_sourceV6, s_destinationV6, 53000, 443);
        Assert.False(IPFragment.IsFragment(hopByHopOnly));
    }

    [Fact]
    public void RejectsNonIpAndTruncatedFrames()
    {
        var arp = new byte[42];
        BinaryPrimitives.WriteUInt16BigEndian(arp.AsSpan(12, 2), 0x0806);
        Assert.False(IPFragment.IsFragment(arp));
        Assert.False(IPFragment.IsFragment(new byte[13]));
        Assert.False(IPFragment.IsFragment(new byte[33]));
    }

    [Fact]
    public void ReadsAddressPairOfBothFamilies()
    {
        var v4 = TcpFragmentHandlingTests.BuildIpv4Fragment(s_sourceV4, s_destinationV4, 53000, 443);
        Assert.True(IPFragment.TryReadAddressPair(v4, out var v4Source, out var v4Destination));
        Assert.Equal(IPAddressValue.From(s_sourceV4), v4Source);
        Assert.Equal(IPAddressValue.From(s_destinationV4), v4Destination);

        var v6 = TcpFragmentHandlingTests.BuildIpv6Fragment(s_sourceV6, s_destinationV6, 53000, 443);
        Assert.True(IPFragment.TryReadAddressPair(v6, out var v6Source, out var v6Destination));
        Assert.Equal(IPAddressValue.From(s_sourceV6), v6Source);
        Assert.Equal(IPAddressValue.From(s_destinationV6), v6Destination);

        Assert.False(IPFragment.TryReadAddressPair(new byte[20], out _, out _));
        var arp = new byte[42];
        BinaryPrimitives.WriteUInt16BigEndian(arp.AsSpan(12, 2), 0x0806);
        Assert.False(IPFragment.TryReadAddressPair(arp, out _, out _));
    }

    private static byte[] WithIpv4Flags(ushort flags)
    {
        var frame = FrameBuilders.BuildIpv4TcpFrame(s_sourceV4, s_destinationV4, 53000, 443);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(20, 2), flags);
        return frame;
    }
}
