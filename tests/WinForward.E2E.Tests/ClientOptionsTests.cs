using WinForward.E2E.Cli;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The client's argument surface (D2/D6, and the operator-typo case behind D14.23's leading-dash
/// rule). These are the checks that run before any plan is read, so a rejected argument can only be
/// a usage error.
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
}
