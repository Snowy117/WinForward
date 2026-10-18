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

    // 7 long-form plans, 5 short-form ones, the Windows shape-preserving compression and the 5 release
    // plans. The count is asserted so that a plan moved out of these directories fails the suite
    // instead of quietly shrinking the set under test.
    [Fact]
    public void EveryShippedPlanLoads()
    {
        var plans = ShippedPlanPaths();

        Assert.Equal(18, plans.Length);
        foreach (var plan in plans)
        {
            Assert.True(PlanFile.TryLoad(plan, out var arms, out var planBytes, out var error), $"{plan}: {error}");
            Assert.Null(error);
            Assert.NotEmpty(planBytes);
            Assert.NotEmpty(arms);
        }
    }

    // A loss or mix arm classifies arrivals against a loss window W, and a plan that declares none
    // silently measures against the 200 ms default instead. base passes W through from its own entry
    // and dns has no such arm, so only these two kinds are held to declaring it.
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

    /// <summary>
    /// The calibration-only positive control: the reference row's latency and loss arms at release
    /// durations and rates, with no MIX/REL/THRU breadth that the sensitivity question does not need.
    /// </summary>
    [Fact]
    public void TheSensitivityPlanRunsLatencyAndLossAtTheReleaseCaliber()
    {
        var plan = Path.Combine(RepoPaths.PlansDirectory, "sensitivity-plan.json");

        Assert.True(PlanFile.TryLoad(plan, out var arms, out _, out var error), error);
        Assert.Equal("LAT,LOSS", string.Join(',', arms.Select(arm => arm.Name)));

        var latency = arms[0];
        Assert.Equal(30, latency.Seconds);
        Assert.Equal(20, latency.RatePerSecond);
        Assert.Equal("tcp+udp", latency.Protocol);

        var loss = arms[1];
        Assert.Equal(120, loss.Seconds);
        Assert.Equal(500, loss.RatePerSecond);
        Assert.Equal(200, loss.LossWindowMs);
    }

    /// <summary>
    /// The reliability arm's fault mix is the one plan key that carries the target's stall mode.
    /// Canonical and release both declare it, so the release plan stays a duration-only variant and a
    /// plan that quietly dropped the mode would fail here.
    /// </summary>
    [Fact]
    public void BothFullPlansRunTheStallFaultMode()
    {
        string[] directories = [RepoPaths.PlansDirectory, RepoPaths.ReleasePlansDirectory];
        foreach (var directory in directories)
        {
            var plan = Path.Combine(directory, "full-plan.json");
            Assert.True(PlanFile.TryLoad(plan, out var arms, out _, out var error), error);

            var modeMix = arms.Single(arm => arm.Name == "REL").ModeMix;
            Assert.Contains("stall=25", modeMix, StringComparison.Ordinal);
        }
    }

    private static string[] ShippedPlanPaths() =>
        [.. Directory.GetFiles(RepoPaths.PlansDirectory, "*.json")
            .Concat(Directory.GetFiles(RepoPaths.ShortPlansDirectory, "*.json"))
            .Concat(Directory.GetFiles(RepoPaths.WindowsPlansDirectory, "*.json"))
            .Concat(Directory.GetFiles(RepoPaths.ReleasePlansDirectory, "*-plan.json"))
            .Order(StringComparer.Ordinal)];
}
