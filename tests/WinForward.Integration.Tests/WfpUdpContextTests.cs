using System.Net;
using WinForward.Userland.Relay;
using Xunit;

namespace WinForward.Integration.Tests;

/// <summary>
/// The cmsg chain walk: which control layout yields a redirect context, and which chain shapes must be
/// refused rather than guessed at.
/// </summary>
public sealed class WfpUdpContextTests
{
    [Theory]
    [InlineData(UdpRedirectTestData.LevelIp)]
    [InlineData(UdpRedirectTestData.LevelIpv6)]
    public void AContextAtEitherProtocolLevelIsExtracted(int level)
    {
        var context = UdpRedirectTestData.Context();
        var control = UdpRedirectTestData.Cmsg(context, level);

        Assert.True(WfpUdpContext.TryParseControl(control, out var payload, out var parsedLevel));

        Assert.Equal(context, payload.ToArray());
        Assert.Equal(level, parsedLevel);
    }

    /// <summary>
    /// The level names the delivering layer, not the context's family: a v4-mapped connected
    /// association is redirected on the IPv6 layer while the driver has already normalized its
    /// destination to family 4. Both halves must be accepted together.
    /// </summary>
    [Fact]
    public void ALevel41CmsgCarryingAFamily4ContextDecodesAsIpv4()
    {
        var context = UdpRedirectTestData.Context(family: 4, address: "192.0.2.53", port: 40010);
        var control = UdpRedirectTestData.Cmsg(context, UdpRedirectTestData.LevelIpv6);

        Assert.True(WfpUdpContext.TryParseControl(control, out var payload, out var level));
        Assert.True(RedirectDestination.TryParse(payload, out var destination, out var error), error);

        Assert.Equal(UdpRedirectTestData.LevelIpv6, level);
        Assert.Equal(17, destination!.Protocol);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.53"), 40010), destination.EndPoint);
    }

    [Fact]
    public void AContextAfterAnUnrelatedCmsgIsFound()
    {
        var unrelated = UdpRedirectTestData.Cmsg([1, 2, 3, 4], UdpRedirectTestData.LevelIp, type: 1);
        var context = UdpRedirectTestData.Context();
        var control = UdpRedirectTestData.Chain(unrelated, UdpRedirectTestData.Cmsg(context, UdpRedirectTestData.LevelIp));

        Assert.True(WfpUdpContext.TryParseControl(control, out var payload, out _));

        Assert.Equal(context, payload.ToArray());
    }

    [Fact]
    public void AContextCmsgOfAnotherTypeIsNotTheRedirectContext()
    {
        var control = UdpRedirectTestData.Cmsg(UdpRedirectTestData.Context(), UdpRedirectTestData.LevelIp, type: 2);

        Assert.False(WfpUdpContext.TryParseControl(control, out var payload, out var level));
        Assert.True(payload.IsEmpty);
        Assert.Equal(-1, level);
    }

    [Fact]
    public void AnEmptyControlBufferHasNoContext()
    {
        Assert.False(WfpUdpContext.TryParseControl([], out _, out _));
    }

    [Fact]
    public void TwoContextCmsgsAreRefusedRatherThanGuessed()
    {
        var first = UdpRedirectTestData.Cmsg(UdpRedirectTestData.Context(), UdpRedirectTestData.LevelIp);
        var second = UdpRedirectTestData.Cmsg(UdpRedirectTestData.Context(port: 40010), UdpRedirectTestData.LevelIpv6);
        var control = UdpRedirectTestData.Chain(first, second);

        Assert.False(WfpUdpContext.TryParseControl(control, out var payload, out _));
        Assert.True(payload.IsEmpty);
    }

    [Fact]
    public void ACmsgLengthPastTheBufferIsMalformed()
    {
        var control = UdpRedirectTestData.Cmsg(UdpRedirectTestData.Context(), UdpRedirectTestData.LevelIp);
        UdpRedirectTestData.WriteDeclaredLength(control, UdpRedirectTestData.ReadDeclaredLength(control) + 64);

        Assert.False(WfpUdpContext.TryParseControl(control, out _, out _));
    }

    [Fact]
    public void ACmsgLengthBelowItsHeaderIsMalformed()
    {
        var control = UdpRedirectTestData.Cmsg(UdpRedirectTestData.Context(), UdpRedirectTestData.LevelIp);
        UdpRedirectTestData.WriteDeclaredLength(control, 4);

        Assert.False(WfpUdpContext.TryParseControl(control, out _, out _));
    }

    [Fact]
    public void AContextPayloadShorterThanTheAbiIsRefused()
    {
        var shortContext = UdpRedirectTestData.Context(length: UdpRedirectTestData.ContextSize - 1);
        var control = UdpRedirectTestData.Cmsg(shortContext, UdpRedirectTestData.LevelIp);

        Assert.False(WfpUdpContext.TryParseControl(control, out _, out _));
    }

    [Theory]
    [InlineData(0x31434657U, (ushort)1, 17, true)]
    [InlineData(0x31434658U, (ushort)1, 17, false)]
    [InlineData(0x31434657U, (ushort)2, 17, false)]
    [InlineData(0x31434657U, (ushort)1, 6, true)]
    [InlineData(0x31434657U, (ushort)1, 1, false)]
    public void TheExtractedPayloadIsValidatedByTheSingleAbiDecoder(uint magic, ushort version, byte protocol, bool expected)
    {
        var context = UdpRedirectTestData.Context(protocol: protocol, magic: magic, version: version);
        var control = UdpRedirectTestData.Cmsg(context, UdpRedirectTestData.LevelIp);

        Assert.True(WfpUdpContext.TryParseControl(control, out var payload, out _));
        Assert.Equal(expected, RedirectDestination.TryParse(payload, out _, out _));
    }

    [Fact]
    public void AnOverLongContextPayloadStillDecodesItsFirstBytes()
    {
        var longContext = UdpRedirectTestData.Context(length: UdpRedirectTestData.ContextSize + 4);
        var control = UdpRedirectTestData.Cmsg(longContext, UdpRedirectTestData.LevelIp);

        Assert.True(WfpUdpContext.TryParseControl(control, out var payload, out _));
        Assert.Equal(longContext.Length, payload.Length);
        Assert.True(RedirectDestination.TryParse(payload, out var destination, out var error), error);
        Assert.Equal(IPAddress.Parse("192.0.2.53"), destination!.EndPoint.Address);
    }
}
