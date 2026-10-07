using WinForward.E2E.Cli;
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
            TargetOptions.TryCreate(
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
        Assert.False(TargetOptions.TryCreate(["--tcp-port", "40010", "--dns-port", "40010"], out _, out var error));

        Assert.Contains("--dns-port 40010", error, StringComparison.Ordinal);
        Assert.Contains("--tcp-port 40010", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ADnsPortEqualToTheUdpPortIsRefusedWithBothOptionsNamed()
    {
        Assert.False(TargetOptions.TryCreate(["--udp-port", "40010", "--dns-port", "40010"], out _, out var error));

        Assert.Contains("--dns-port 40010", error, StringComparison.Ordinal);
        Assert.Contains("--udp-port 40010", error, StringComparison.Ordinal);
    }

    // The dns port check runs first, so the older additional-listener check still sees a dns port that
    // is itself distinct from the tcp and udp ports.
    [Fact]
    public void ASecondDnsListenerOnAnAlreadyUsedPortIsRefused()
    {
        Assert.False(
            TargetOptions.TryCreate(["--tcp-port", "40010", "--udp-port", "40011", "--dns-port", "53", "--dns-alt-port", "40011"], out _, out var error));

        Assert.Contains("the additional dns port must differ", error, StringComparison.Ordinal);
    }

    [Fact]
    public void APortOutsideTheSixteenBitRangeIsRefused()
    {
        Assert.False(TargetOptions.TryCreate(["--tcp-port", "40010", "--dns-port", "65536"], out _, out var error));

        Assert.Contains("ports must be in the range 1..65535", error, StringComparison.Ordinal);
    }

    // The walk-level refusals, which E2-d moved into the shared parser: both verbs must still refuse
    // a name they do not declare and a declared option at the end of the line.
    [Fact]
    public void AnUnknownOptionIsRefusedWithTheArgumentAsWritten()
    {
        Assert.False(TargetOptions.TryCreate(["--bind", "127.0.0.1", "--typo", "3"], out _, out var error));

        Assert.Equal("unknown argument '--typo'", error);
    }

    [Fact]
    public void AnOptionWithNothingLeftToReadIsRefusedWithItsName()
    {
        Assert.False(TargetOptions.TryCreate(["--ledger"], out _, out var error));

        Assert.Equal("missing value for '--ledger'", error);
    }

    // The client refuses a string value that starts with '-' because it is usually a forgotten
    // option; the target does not, and the shared walk must not have imported the rule with the rest
    // of the parsing (D14.23). A target label that looks like an option is a label.
    [Fact]
    public void AStringValueThatLooksLikeAnOptionIsStillAccepted()
    {
        Assert.True(TargetOptions.TryCreate(["--label", "--out"], out var options, out var error), error);

        Assert.Equal("--out", options.Label);
    }
}
