using WinForward.E2E.Cli;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The client's argument surface, including the leading-dash refusal an operator typo provokes.
/// These are the checks that run before any plan is read, so a rejected argument can only be a
/// usage error.
/// </summary>
public sealed class ClientOptionsTests
{
    [Theory]
    [InlineData("--plan=")]
    [InlineData("--plan")]
    public void AnEmptyPlanArgumentIsAUsageError(string argument)
    {
        // The bare --plan form makes the next argument its value, so it needs one to swallow.
        var args = argument.EndsWith('=')
            ? new[] { "--target", "127.0.0.1", "--out", "/tmp/out", argument }
            : ["--target", "127.0.0.1", "--out", "/tmp/out", argument, string.Empty];

        Assert.False(ClientOptions.TryCreate(args, out _, out var error));
        Assert.Contains("--plan", error, StringComparison.Ordinal);
    }

    [Fact]
    public void OmittingThePlanKeepsTheBuiltInPlan()
    {
        Assert.True(ClientOptions.TryCreate(["--target", "127.0.0.1", "--out", "/tmp/out"], out var options, out var error), error);

        Assert.Null(options.PlanPath);
    }

    [Fact]
    public void AnEmptySamplerProcessIsAUsageError()
    {
        Assert.False(ClientOptions.TryCreate(
            ["--target", "127.0.0.1", "--out", "/tmp/out", "--sampler-process="],
            out _,
            out var error));

        Assert.Contains("--sampler-process", error, StringComparison.Ordinal);
    }

    // A string option takes the next argument as its value, so a forgotten value eats the option that
    // follows it and the run is configured by accident; the message has to name both.
    [Fact]
    public void AValueThatLooksLikeAnOptionIsAUsageError()
    {
        Assert.False(ClientOptions.TryCreate(
            ["--target", "127.0.0.1", "--out", "/tmp/out", "--label", "--out", "x"],
            out _,
            out var error));

        Assert.Contains("--label", error, StringComparison.Ordinal);
        Assert.Contains("'--out'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ANumberOptionStillRejectsANonNumericValue()
    {
        Assert.False(ClientOptions.TryCreate(
            ["--target", "127.0.0.1", "--out", "/tmp/out", "--tcp-port", "--udp-port"],
            out _,
            out var error));

        Assert.Contains("'--udp-port' is not a port number in 1..65535", error, StringComparison.Ordinal);
    }

    // The two refusals that belong to the walk rather than to an option: a name the verb does not
    // declare, and a declared option at the end of the line with nothing left to read.
    [Fact]
    public void AnUnknownOptionIsRefusedWithTheArgumentAsWritten()
    {
        Assert.False(ClientOptions.TryCreate(["--target", "127.0.0.1", "--out", "/tmp/out", "--typo=3"], out _, out var error));

        Assert.Equal("unknown argument '--typo=3'", error);
    }

    [Fact]
    public void AnOptionWithNothingLeftToReadIsRefusedWithItsName()
    {
        Assert.False(ClientOptions.TryCreate(["--target", "127.0.0.1", "--out", "/tmp/out", "--label"], out _, out var error));

        Assert.Equal("missing value for '--label'", error);
    }
}
