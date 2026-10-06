using Microsoft.Extensions.Logging;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Runtime.Capture.Tests;

/// <summary>
/// The capture lifecycle milestones an operator reads at <c>info</c> (task 10-06 R7): the resolved
/// capture scope and every generation start, each carrying the adapter count and identity.
/// </summary>
public sealed class CaptureMilestoneLoggingTests
{
    [Fact]
    public async Task ScopeResolutionAndGenerationStartAreLoggedAtInformationWithAdapterIdentity()
    {
        await using var harness = new CaptureRunnerHarness(
            [CaptureRunnerFakes.AdapterItem("id-a", 101), CaptureRunnerFakes.AdapterItem("id-b", 202)],
            CaptureRunnerFakes.UnconstrainedPolicy());
        harness.Start();
        await harness.WaitForGenerationStartedAsync(0).ConfigureAwait(false);

        var resolved = Assert.Single(harness.Logger.Events, entry => string.Equals(entry.Name, "capture.scope.resolved", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, resolved.Level);
        Assert.Equal("2", CaptureRunnerHarness.FieldValue(resolved.Fields, "Adapters"));
        Assert.Equal("id-a(id-a); id-b(id-b)", CaptureRunnerHarness.FieldValue(resolved.Fields, "Scope"));

        var started = Assert.Single(harness.Logger.Events, entry => string.Equals(entry.Name, "capture.generation.started", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, started.Level);
        Assert.Equal("2", CaptureRunnerHarness.FieldValue(started.Fields, "Adapters"));
        Assert.Equal("id-a(id-a); id-b(id-b)", CaptureRunnerHarness.FieldValue(started.Fields, "Scope"));
    }
}
