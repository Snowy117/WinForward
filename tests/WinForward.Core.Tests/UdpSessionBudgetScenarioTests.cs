using System.Globalization;
using System.Net;
using WinForward.Benchmarks;
using WinForward.Benchmarks.Stability;
using WinForward.Configuration;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class UdpSessionBudgetScenarioTests
{
    private static readonly TimeSpan s_idle = ConfigurationLoader.DefaultUdpSessionIdleTimeout;
    private static readonly TimeSpan s_sweep = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData("udpSessionBudget")]
    [InlineData("udpsessionbudget")]
    [InlineData("sessionBudget")]
    [InlineData("budget")]
    public void SessionBudgetTokenIsAccepted(string token)
    {
        Assert.Equal(SoakScenario.SessionBudget, SoakOptions.Parse(["--scenario", token]).Scenario);
    }

    [Fact]
    public void SessionBudgetDefaultsMatchTheValidatedConfiguration()
    {
        var options = SoakOptions.Parse(["--scenario", "udpSessionBudget"]);
        Assert.Equal(20, options.Rate);
        Assert.Equal(ConfigurationLoader.DefaultUdpSessionCapacity, options.Capacity);
        // 90 s clears the retention ceiling's discrimination minimum (63 s at the default rate) with
        // headroom, so the shipped invocation cannot record a vacuous retention claim.
        Assert.Equal(90, options.ChurnSeconds);
        Assert.Equal(120, options.DrainSeconds);
        Assert.False(options.RequirePooling);
    }

    [Fact]
    public void SessionBudgetKnobsOverrideTheirDefaults()
    {
        var options = SoakOptions.Parse(["--scenario", "udpSessionBudget", "--rate", "100", "--capacity", "2048", "--churn-seconds", "3600", "--drain-seconds", "300"]);
        Assert.Equal(100, options.Rate);
        Assert.Equal(2_048, options.Capacity);
        Assert.Equal(3_600, options.ChurnSeconds);
        Assert.Equal(300, options.DrainSeconds);
    }

    [Fact]
    public void SessionBudgetRequirePoolingIsOptIn()
    {
        Assert.False(SoakOptions.Parse(["--scenario", "udpSessionBudget"]).RequirePooling);
        Assert.True(SoakOptions.Parse(["--scenario", "udpSessionBudget", "--require-pooling"]).RequirePooling);
    }

    [Theory]
    [InlineData("--rate", "0")]
    [InlineData("--rate", "-3")]
    [InlineData("--capacity", "0")]
    [InlineData("--churn-seconds", "0")]
    [InlineData("--drain-seconds", "0")]
    public void SessionBudgetRejectsNonPositiveKnobs(string name, string value)
    {
        Assert.Throws<ArgumentException>(() => SoakOptions.Parse(["--scenario", "udpSessionBudget", name, value]));
    }

    [Theory]
    [InlineData("16385")]
    [InlineData("1000000")]
    [InlineData("999999999999")]
    public void SessionBudgetRejectsACapacityOutsideTheProductRange(string value)
    {
        // The product accepts udpSessionCapacity 1..16384; a larger --capacity would make the soak's
        // "strictly below --capacity" assertion vacuous, so it is refused at parse time.
        var exception = Assert.Throws<ArgumentException>(() => SoakOptions.Parse(["--scenario", "udpSessionBudget", "--capacity", value]));
        Assert.Contains($"1..{ConfigurationLoader.DefaultUdpSessionCapacity}", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("16384")]
    public void SessionBudgetAcceptsTheProductRangeBoundaries(string value)
    {
        Assert.Equal(int.Parse(value, CultureInfo.InvariantCulture), SoakOptions.Parse(["--scenario", "udpSessionBudget", "--capacity", value]).Capacity);
    }

    [Fact]
    public void SessionBudgetSelectionExpandsToTheScenario()
    {
        var scenarios = SoakRunner.SelectScenarios(SoakScenario.SessionBudget);
        var (name, _) = Assert.Single(scenarios);
        Assert.Equal("udpSessionBudget", name);
        Assert.DoesNotContain(SoakRunner.SelectScenarios(SoakScenario.All), entry => string.Equals(entry.Name, "udpSessionBudget", StringComparison.Ordinal));
    }

    [Fact]
    public void RetentionCeilingFollowsRateIdleAndSweep()
    {
        var margin = SessionBudgetMath.SteadyStateMargin(100);
        Assert.Equal((int)Math.Ceiling(100 * (s_idle + (2 * s_sweep)).TotalSeconds) + margin, SessionBudgetMath.SteadyStateSessionCeiling(100, s_idle, s_sweep, margin));
        // The ceiling is linear in the arrival rate: twice the rate, twice the retained population.
        Assert.Equal(
            SessionBudgetMath.SteadyStateSessionCeiling(100, s_idle, s_sweep, margin) * 2,
            SessionBudgetMath.SteadyStateSessionCeiling(200, s_idle, s_sweep, margin * 2));
    }

    [Fact]
    public void RetentionCeilingRejectsAPopulationThatAccumulates()
    {
        var margin = SessionBudgetMath.SteadyStateMargin(100);
        var ceiling = SessionBudgetMath.SteadyStateSessionCeiling(100, s_idle, s_sweep, margin);
        // Retention bounds the live set: the steady-state population is the arrivals inside
        // idle + 2 x sweep, so it fits.
        Assert.True(100 * (s_idle + (2 * s_sweep)).TotalSeconds <= ceiling);
        // Two sweeps behind the retention window (a stale tick, or a retention regression) does not.
        Assert.True(100 * (s_idle + (4 * s_sweep)).TotalSeconds > ceiling);
        // The un-bounded shape — every flow of a one-hour run still live — is orders of magnitude out.
        Assert.True(100 * 3_600 > ceiling);
    }

    [Fact]
    public void SteadyStateMarginAllowsArrivalJitterButStaysBounded()
    {
        Assert.Equal(32, SessionBudgetMath.SteadyStateMargin(1));
        Assert.Equal(2 * 100, SessionBudgetMath.SteadyStateMargin(100));
        Assert.True(SessionBudgetMath.SteadyStateMargin(1_000) < SessionBudgetMath.SteadyStateSessionCeiling(1_000, s_idle, s_sweep, 0));
    }

    [Fact]
    public void DescriptorBudgetFitsOneRelaySocketPerSessionAndOneControlConnectionPerAssociation()
    {
        // The pooled shape over a population inside the shared budget: one relay socket per live
        // session plus one control connection per association (16 flows per association).
        const long sessions = 3_000;
        const long associations = 188;
        const long measured = sessions + associations;
        Assert.True(measured <= SessionBudgetMath.FileDescriptorBudget(sessions, associations));
        // A second descriptor per session — a per-flow control connection that is NOT charged as an
        // association, or a leaked socket — does not fit the 1.25-per-session slack.
        Assert.True((2 * sessions) > SessionBudgetMath.FileDescriptorBudget(sessions, associations));
        // The budget is expressed in the live population only, so a run with ten times the cumulative
        // flows but the same steady state costs the same.
        Assert.Equal((long)Math.Ceiling(sessions * SessionBudgetMath.DescriptorSlackPerSession) + associations, SessionBudgetMath.FileDescriptorBudget(sessions, associations));
    }

    [Fact]
    public void SharedAssociationCeilingSeesPerFlowConnections()
    {
        // Pooled: 187 live sessions share 12 associations (16 flows each) under the 16-association cap.
        Assert.True(12 <= SessionBudgetMath.SharedAssociationCeiling(187, 16, 16));
        // Per-flow: one association per session cannot hide inside the fan-out ceiling.
        Assert.True(187 > SessionBudgetMath.SharedAssociationCeiling(187, 16, 16));
        Assert.True(374 > SessionBudgetMath.SharedAssociationCeiling(374, 16, 16));
    }

    [Fact]
    public void PerSessionRatioIsZeroForAnEmptyPopulation()
    {
        Assert.Equal(0.0, SessionBudgetMath.Ratio(0, 0));
        Assert.Equal(0.0, SessionBudgetMath.Ratio(5, 0));
        Assert.Equal(0.75, SessionBudgetMath.Ratio(3, 4), 6);
        Assert.Equal(-0.5, SessionBudgetMath.Ratio(-1, 2), 6);
    }

    [Fact]
    public async Task ResponseSinkSplitsWarmupFromChurnAndIgnoresDuplicates()
    {
        var sink = new SessionBudgetSink(churnOffset: 2, flowCapacity: 4);
        var flow = BenchmarkShared.CreateFlowKey(0);
        Assert.Equal(0, sink.WarmupFirstResponses);
        Assert.Equal(0, sink.ChurnFirstResponses);

        await InjectAsync(sink, flow, flowId: 0);
        await InjectAsync(sink, flow, flowId: 1);
        Assert.Equal(2, sink.WarmupFirstResponses);
        Assert.Equal(0, sink.ChurnFirstResponses);

        await InjectAsync(sink, flow, flowId: 2);
        Assert.Equal(1, sink.ChurnFirstResponses);
        // A duplicate echo of a flow that already answered must not count twice: the loss
        // accounting is per flow, not per datagram.
        await InjectAsync(sink, flow, flowId: 2);
        Assert.Equal(1, sink.ChurnFirstResponses);
        // Out-of-range flow ids belong to another run and are ignored rather than faulting.
        await InjectAsync(sink, flow, flowId: 4);
        Assert.Equal(1, sink.ChurnFirstResponses);
        Assert.NotEqual(0, sink.FirstResponseTicks(2));
        Assert.Equal(0, sink.FirstResponseTicks(3));
    }

    [Fact]
    public void DescriptorSamplerReadsThisProcess()
    {
        Assert.True(ProcessResourceSampler.OpenFileDescriptors() > 0);
        using var sampler = new ProcessResourceSampler(inProcessServer: null);
        var sample = sampler.Sample();
        Assert.True(sample.ProxyFileDescriptors > 0);
        Assert.Equal(sample.RawFileDescriptors, sample.ProxyFileDescriptors);
        Assert.Equal(0, sample.HarnessServerConnections);
        Assert.True(sample.WorkingSetBytes > 0);
    }

    private static ValueTask InjectAsync(SessionBudgetSink sink, FlowKey flow, int flowId)
    {
        var payload = new byte[16];
        DatagramHeader.Write(payload, sequence: 1, flow: flowId);
        var remote = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        return sink.InjectAsync(flow, remote, payload, MacAddress.Invalid, CancellationToken.None);
    }
}
