using WinForward.E2E.Target;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The target's command-line contract. The port checks are the ones that matter here: the dns
/// responder binds its port on tcp and udp at once, so a dns port that collides with one of the other
/// listeners is a usage error rather than a run where two listeners split the traffic.
/// </summary>
public sealed class TargetOptionsTests
{
    // The campaign's own command line (scripts/start-targets.sh) shares one number between the tcp
    // and udp ports by design, so the dns collision check must not be a "no two ports may be equal"
    // rule; this is the regression guard for that.
    [Fact]
    public void TheSameTcpAndUdpPortIsAccepted()
    {
        Assert.True(
            TargetRunner.TryCreate(
                ["--bind", "127.0.0.1", "--tcp-port", "40010", "--udp-port", "40010", "--dns-port", "53", "--dns-alt-port", "40053"],
                out var options,
                out var error),
            error);

        Assert.Equal(40010, options.TcpPort);
        Assert.Equal(40010, options.UdpPort);
    }

    [Fact]
    public void ADnsPortEqualToTheTcpPortIsRefusedWithBothOptionsNamed()
    {
        Assert.False(TargetRunner.TryCreate(["--tcp-port", "40010", "--dns-port", "40010"], out _, out var error));

        Assert.Contains("--dns-port 40010", error, StringComparison.Ordinal);
        Assert.Contains("--tcp-port 40010", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ADnsPortEqualToTheUdpPortIsRefusedWithBothOptionsNamed()
    {
        Assert.False(TargetRunner.TryCreate(["--udp-port", "40010", "--dns-port", "40010"], out _, out var error));

        Assert.Contains("--dns-port 40010", error, StringComparison.Ordinal);
        Assert.Contains("--udp-port 40010", error, StringComparison.Ordinal);
    }

    // The dns port check runs first, so the older additional-listener check still sees a dns port that
    // is itself distinct from the tcp and udp ports.
    [Fact]
    public void ASecondDnsListenerOnAnAlreadyUsedPortIsRefused()
    {
        Assert.False(
            TargetRunner.TryCreate(["--tcp-port", "40010", "--udp-port", "40011", "--dns-port", "53", "--dns-alt-port", "40011"], out _, out var error));

        Assert.Contains("the additional dns port must differ", error, StringComparison.Ordinal);
    }

    [Fact]
    public void APortOutsideTheSixteenBitRangeIsRefused()
    {
        Assert.False(TargetRunner.TryCreate(["--tcp-port", "40010", "--dns-port", "65536"], out _, out var error));

        Assert.Contains("ports must be in the range 1..65535", error, StringComparison.Ordinal);
    }
}
