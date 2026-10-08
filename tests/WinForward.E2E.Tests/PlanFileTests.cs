using WinForward.E2E.Client;
using Xunit;

namespace WinForward.E2E.Tests;

public sealed class PlanFileTests
{
    [Fact]
    public void TheBuiltInPlanLoadsWithTheEightDocumentedArms()
    {
        Assert.True(PlanFile.TryLoad(null, out var arms, out var planBytes, out var error), error);
        Assert.Null(error);
        Assert.NotEmpty(planBytes);
        Assert.Equal(
            "LAT,LOSS,REL,THRU,DNS,MIX,IDLE,BASE",
            string.Join(',', arms.Select(arm => arm.Name)));
    }

    // 6 long-form plans, 5 short-form ones and the Windows shape-preserving compression. The count is
    // asserted so that a plan moved out of these directories fails the suite instead of quietly
    // shrinking the set under test.
    [Fact]
    public void EveryShippedPlanLoads()
    {
        var plans = ShippedPlanPaths();

        Assert.Equal(12, plans.Length);
        foreach (var plan in plans)
        {
            Assert.True(PlanFile.TryLoad(plan, out var arms, out var planBytes, out var error), $"{plan}: {error}");
            Assert.Null(error);
            Assert.NotEmpty(planBytes);
            Assert.NotEmpty(arms);
        }
    }

    // #6: a loss or mix arm classifies arrivals against a loss window W, and a plan that declares
    // none silently measures against the 200 ms default instead. base passes W through from its own
    // entry and dns has no such arm, so only these two kinds are held to declaring it (D14.2).
    [Fact]
    public void EveryLossAndMixArmDeclaresItsLossWindow()
    {
        foreach (var plan in ShippedPlanPaths())
        {
            Assert.True(PlanFile.TryLoad(plan, out var arms, out _, out var error), $"{plan}: {error}");
            foreach (var arm in arms.Where(arm => arm.Kind is "loss" or "mix"))
            {
                Assert.True(arm.LossWindowMs > 0, $"{plan}: arm '{arm.Name}' (kind '{arm.Kind}') does not declare lossWindowMs");
            }
        }
    }

    [Fact]
    public void TheBuiltInPlanUsesTheDocumentedDefaults()
    {
        Assert.True(PlanFile.TryLoad(null, out var arms, out _, out var error), error);

        var latency = arms.Single(arm => arm.Name == "LAT");
        Assert.Equal("latency", latency.Kind);
        Assert.Equal(60, latency.Seconds);
        Assert.Equal(20, latency.RatePerSecond);
        Assert.Equal(120, latency.PayloadBytes);
        Assert.Equal("tcp+udp", latency.Protocol);

        var loss = arms.Single(arm => arm.Name == "LOSS");
        Assert.Equal("loss", loss.Kind);
        Assert.Equal(120, loss.Seconds);
        Assert.Equal(500, loss.RatePerSecond);

        // A key the built-in plan does not declare keeps the arm's own default rather than a
        // stale value from another plan.
        Assert.Equal(0, loss.LossWindowMs);
        Assert.Equal(ArmSpec.DefaultModeMix, arms.Single(arm => arm.Name == "REL").ModeMix);
    }

    [Fact]
    public void AGivenPathOverridesTheBuiltInPlan()
    {
        var plan = Path.Combine(RepoPaths.ShortPlansDirectory, "base-plan.json");

        Assert.True(PlanFile.TryLoad(plan, out var arms, out var planBytes, out var error), error);
        Assert.Null(error);
        Assert.Single(arms);
        Assert.Equal("BASE", arms[0].Name);
        Assert.True(planBytes.Length > 0);
    }

    private static string[] ShippedPlanPaths() =>
        [.. Directory.GetFiles(RepoPaths.PlansDirectory, "*.json")
            .Concat(Directory.GetFiles(RepoPaths.ShortPlansDirectory, "*.json"))
            .Concat(Directory.GetFiles(RepoPaths.WindowsPlansDirectory, "*.json"))
            .Order(StringComparer.Ordinal)];
}
