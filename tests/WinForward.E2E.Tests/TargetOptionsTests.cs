using System.Globalization;
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

    // The walk-level refusals the shared parser owns: both verbs must still refuse a name they do
    // not declare and a declared option at the end of the line.
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
    // of the parsing. A target label that looks like an option is a label.
    [Fact]
    public void AStringValueThatLooksLikeAnOptionIsStillAccepted()
    {
        Assert.True(TargetOptions.TryCreate(["--label", "--out"], out var options, out var error), error);

        Assert.Equal("--out", options.Label);
    }

    // A run that does not declare the count runs the formula it always ran; the option exists so a run
    // can declare another one, not so the default moves.
    [Fact]
    public void TheUndeclaredUdpReceiverCountIsTheFormulaTheTargetAlwaysUsed()
    {
        Assert.True(TargetOptions.TryCreate(["--bind", "127.0.0.1"], out var options, out var error), error);

        Assert.Equal(Math.Clamp(Environment.ProcessorCount / 2, 2, 8), options.UdpReceivers);
    }

    [Fact]
    public void ADeclaredUdpReceiverCountIsTaken()
    {
        Assert.True(TargetOptions.TryCreate(["--udp-receivers", "3"], out var options, out var error), error);

        Assert.Equal(3, options.UdpReceivers);
    }

    // The lowest count a listener can run with is 1: the option decides how much receive concurrency a
    // run gets, and a single receive loop is a count a run may declare.
    [Fact]
    public void TheLowestReceiverCountTheRefusalNamesIsAccepted()
    {
        Assert.True(TargetOptions.TryCreate(["--udp-receivers", "1"], out var options, out var error), error);

        Assert.Equal(1, options.UdpReceivers);
    }

    // Three kinds of refusal, one fact each: a value that is not a decimal number, one that would leave
    // the listener with no loop at all, and one above the bound. Every one of them is a usage error, so
    // the text has to name the value or the range rather than a bare "invalid".
    [Theory]
    [InlineData("abc", "is not a receive-loop count")]
    [InlineData("1.5", "is not a receive-loop count")]
    [InlineData("0", "the udp receive-loop count must be in the range 1..")]
    [InlineData("-1", "is not a receive-loop count")]
    [InlineData("65", "the udp receive-loop count must be in the range 1..")]
    public void AnUdpReceiverCountThatCannotRunIsRefused(string value, string expected)
    {
        Assert.False(TargetOptions.TryCreate(["--udp-receivers", value], out _, out var error));

        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    // The bound is a constant rather than a magic number in the message: the refusal names the same
    // number the parser enforces, so raising one without the other is a test failure.
    [Fact]
    public void TheUdpReceiverBoundIsTheOneTheRefusalNames()
    {
        Assert.True(TargetOptions.TryCreate(["--udp-receivers", TargetOptions.MaxUdpReceivers.ToString(CultureInfo.InvariantCulture)], out var options, out var error), error);
        Assert.Equal(TargetOptions.MaxUdpReceivers, options.UdpReceivers);

        Assert.False(
            TargetOptions.TryCreate(["--udp-receivers", (TargetOptions.MaxUdpReceivers + 1).ToString(CultureInfo.InvariantCulture)], out _, out var refused));

        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"1..{TargetOptions.MaxUdpReceivers}"),
            refused,
            StringComparison.Ordinal);
    }
}
