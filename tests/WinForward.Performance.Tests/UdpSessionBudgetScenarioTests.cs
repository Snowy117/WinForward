using System.Globalization;
using System.Net;
using WinForward.Benchmarks;
using WinForward.Benchmarks.Stability;
using WinForward.Configuration;
using WinForward.Core;
using Xunit;

namespace WinForward.Performance.Tests;

public sealed class UdpSessionBudgetScenarioTests
{
    private static readonly TimeSpan s_idle = ConfigurationLoader.DefaultUdpSessionIdleTimeout;

    /// <summary>The shipped UDP sweep interval: the effective retention floor (the 5 s one-shot class) halved and floored.</summary>
    private static readonly TimeSpan s_sweep = TimeSpan.FromSeconds(5);

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
        // 90 s clears the retention ceiling's discrimination minimum (43 s at the default rate) with
        // headroom, so the shipped invocation cannot record a vacuous retention claim.
        Assert.Equal(90, options.ChurnSeconds);
        Assert.Equal(120, options.DrainSeconds);
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

    [Theory]
    [InlineData("udpSessionBudget")]
    [InlineData("udp")]
    [InlineData("residency")]
    [InlineData("all")]
    public void LocalTargetColumnIsRefusedByTheScenariosThatWouldMisdescribeIt(string scenario)
    {
        var exception = Assert.Throws<ArgumentException>(() => SoakOptions.Parse(["--scenario", scenario, "--target", "local"]));
        Assert.Contains("udpChurn", exception.Message, StringComparison.Ordinal);
        Assert.Contains("udpBurst", exception.Message, StringComparison.Ordinal);
        Assert.Equal(SoakTargetKind.Socks5, SoakOptions.Parse(["--scenario", scenario]).Target);
    }

    [Theory]
    [InlineData("udpChurn")]
    [InlineData("udpBurst")]
    public void LocalTargetColumnIsAcceptedByTheScenariosThatHostIt(string scenario)
    {
        Assert.Equal(SoakTargetKind.Local, SoakOptions.Parse(["--scenario", scenario, "--target", "local"]).Target);
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
    public void DescriptorBudgetFitsThePerFlowRelaySocketAndControlConnection()
    {
        // The shipped shape over a live population: one relay socket and one control connection per
        // session, i.e. two descriptors per session against a budget of ceil(sessions x 1.25) plus
        // one association per session.
        const long sessions = 3_000;
        const long measured = sessions + sessions;
        Assert.True(measured <= SessionBudgetMath.FileDescriptorBudget(sessions, sessions));
        // A third descriptor per session — a leaked socket, or an association whose teardown does not
        // follow its session's — does not fit the 1.25-per-session slack plus the association term.
        Assert.True((3 * sessions) > SessionBudgetMath.FileDescriptorBudget(sessions, sessions));
        // The budget is expressed in the live population only, so a run with ten times the cumulative
        // flows but the same steady state costs the same.
        Assert.Equal((long)Math.Ceiling(sessions * SessionBudgetMath.DescriptorSlackPerSession) + sessions, SessionBudgetMath.FileDescriptorBudget(sessions, sessions));
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
        var keys = CreateFlowKeys(4);
        var sink = new SessionBudgetSink(churnOffset: 2, flowCapacity: 4, flowKeys: keys);
        Assert.Equal(0, sink.WarmupFirstResponses);
        Assert.Equal(0, sink.ChurnFirstResponses);

        await InjectAsync(sink, keys[0], flowId: 0);
        await InjectAsync(sink, keys[1], flowId: 1);
        Assert.Equal(2, sink.WarmupFirstResponses);
        Assert.Equal(0, sink.ChurnFirstResponses);

        sink.BeginChurn();
        await InjectAsync(sink, keys[2], flowId: 2);
        Assert.Equal(1, sink.ChurnFirstResponses);
        // A duplicate echo of a flow that already answered must not count twice: the loss
        // accounting is per flow, not per datagram.
        await InjectAsync(sink, keys[2], flowId: 2);
        Assert.Equal(1, sink.ChurnFirstResponses);
        // Out-of-range flow ids belong to another run and are ignored rather than faulting.
        await InjectAsync(sink, keys[0], flowId: 4);
        Assert.Equal(1, sink.ChurnFirstResponses);
        Assert.NotEqual(0, sink.FirstResponseTicks(2));
        Assert.Equal(0, sink.FirstResponseTicks(3));
    }

    [Fact]
    public async Task ResponseSinkCreditsOnlyTheFlowAReplyArrivedOn()
    {
        var keys = CreateFlowKeys(4);
        var sink = new SessionBudgetSink(churnOffset: 2, flowCapacity: 4, flowKeys: keys);

        // Flow 0's echo arrives on flow 1's relay socket: flow 1 asked for flow 1, and the payload
        // says flow 0 — the shared association's last-sender write path. Neither flow is answered.
        await InjectAsync(sink, keys[1], flowId: 0);
        Assert.Equal(0, sink.WarmupFirstResponses);
        Assert.Equal(0, sink.FirstResponseTicks(0));
        Assert.Equal(0, sink.FirstResponseTicks(1));

        // A second foreign echo for the same flow is the same unanswered flow, not a second one.
        await InjectAsync(sink, keys[1], flowId: 0);
        Assert.Equal(1, sink.Misdelivered);

        // The flow that received the foreign echo and then its own is answered: the misdelivery mark
        // counts flows the wave answered with someone else's echo, and this one was answered after all.
        await InjectAsync(sink, keys[1], flowId: 1);
        Assert.Equal(1, sink.WarmupFirstResponses);
        Assert.NotEqual(0, sink.FirstResponseTicks(1));
    }

    [Fact]
    public async Task ResponseSinkCountsChurnMisdeliverySeparatelyFromTheWarmup()
    {
        var keys = CreateFlowKeys(4);
        var sink = new SessionBudgetSink(churnOffset: 2, flowCapacity: 4, flowKeys: keys);

        // A churn flow receives a reply belonging to the warm-up population, and the warm-up flow it
        // belongs to is still waiting: both are unanswered, and the mark is scoped to the population
        // whose first datagram is still pending.
        await InjectAsync(sink, keys[2], flowId: 0);
        await InjectAsync(sink, keys[0], flowId: 2);
        Assert.Equal(1, sink.ChurnMisdeliveredFlows);
        Assert.Equal(2, sink.Misdelivered);
        Assert.Equal(0, sink.WarmupFirstResponses);
        Assert.Equal(0, sink.ChurnFirstResponses);

        // Opening the churn population clears the marks taken during its own opening window, because
        // the flows that took them have not sent their first churn datagram yet.
        sink.BeginChurn();
        Assert.Equal(0, sink.ChurnMisdeliveredFlows);
        Assert.Equal(1, sink.Misdelivered);

        await InjectAsync(sink, keys[2], flowId: 2);
        Assert.Equal(1, sink.ChurnFirstResponses);
    }

    private static FlowKey[] CreateFlowKeys(int count)
    {
        var keys = new FlowKey[count];
        for (var index = 0; index < keys.Length; index++) keys[index] = BenchmarkShared.CreateFlowKey(index);
        return keys;
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
        // No in-process server to read: the associations column is nobody's observation, not a zero.
        Assert.Null(sample.HarnessControlConnections);
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
