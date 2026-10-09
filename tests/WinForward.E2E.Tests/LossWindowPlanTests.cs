using System.Net.Sockets;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Arms;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Metrics;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The loss window a plan declares is the window the record publishes and classifies against. The
/// fact runs the arm from the plan file, so it pins plan file → loader → arm → <c>metrics.window</c>,
/// and it declares a value other than the 200 ms default: a fact that declared the default would
/// pass for a fallback that always won.
/// </summary>
public sealed class LossWindowPlanTests
{
    private const int DeclaredWindowMs = 137;

    [Fact]
    public async Task TheDeclaredLossWindowIsTheWindowTheRecordPublishes()
    {
        var planPath = RepoPaths.Tier0Plan("loss-window-declared.json");
        Assert.True(PlanFile.TryLoad(planPath, out var arms, out _, out var error), $"{planPath}: {error}");

        var spec = Assert.Single(arms);
        Assert.Equal(DeclaredWindowMs, spec.LossWindowMs);
        Assert.NotEqual(UdpLossMath.DefaultWindowMilliseconds, spec.LossWindowMs);

        var udpPort = ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp);
        using var blackHole = ArmRunFixture.BindBlackHoleUdp(udpPort);
        await using var fixture = ArmRunFixture.Create(
            ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp),
            udpPort,
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp));

        var outcome = await ArmDispatch.RunAsync(fixture.ContextFor(spec));
        var metrics = Assert.IsType<LossMetrics>(outcome.Metrics);

        Assert.Equal(DeclaredWindowMs, metrics.Window);
        Assert.Equal(DeclaredWindowMs, outcome.Parameters.LossWindowMs);
        Assert.Equal(DeclaredWindowMs, outcome.Gates[ArmKeys.Common.Gates.WindowMs]);

        // A fallback that ignored the plan would publish the 200 ms default here and classify the same
        // datagrams against it, so the assertion above is what that mutation turns red.
        Assert.True(metrics.Sent > 0, "the arm sent nothing, so the window classified an empty population");
    }
}
