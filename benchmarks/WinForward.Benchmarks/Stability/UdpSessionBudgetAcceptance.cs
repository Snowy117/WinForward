using System.Globalization;
using System.Runtime.InteropServices;

namespace WinForward.Benchmarks.Stability;

/// <summary>The counters and samples one finished run hands to its acceptance.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct SessionBudgetOutcome(
    IReadOnlyList<SessionBudgetSample> Samples,
    SessionBudgetSample Final,
    long Accepted,
    long Rejected,
    int ChurnPeakSessions,
    int SteadyPeakSessions);

/// <summary>
/// One run's acceptance: the thresholds, the failure list, and the named verdict half the summary
/// row carries — <see cref="RetentionBounded"/>, the population bound the per-flow shape is measured
/// against — so a reader can see which term held without reading the prose. The run owns the phases,
/// the pacing, and the sampling; this type owns every assertion, so the acceptance arithmetic is
/// unit-testable from synthetic samples instead of only from a multi-minute soak.
/// </summary>
internal sealed class SessionBudgetAcceptance
{
    /// <summary>Descriptor delta the steady-state budget and the drain may still show: in-flight teardown, connection hand-off, and sampler noise.</summary>
    private const int DescriptorMargin = 32;

    private readonly SoakOptions _options;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _sweepInterval;
    private readonly int _relayReceiveBufferBytes;
    private readonly List<string> _failures = [];

    internal SessionBudgetAcceptance(
        SoakOptions options,
        TimeSpan idleTimeout,
        TimeSpan sweepInterval,
        int relayReceiveBufferBytes)
    {
        _options = options;
        _idleTimeout = idleTimeout;
        _sweepInterval = sweepInterval;
        _relayReceiveBufferBytes = relayReceiveBufferBytes;
        SteadyStateMargin = SessionBudgetMath.SteadyStateMargin(options.Rate);
        SessionCeiling = SessionBudgetMath.SteadyStateSessionCeiling(options.Rate, idleTimeout, sweepInterval, SteadyStateMargin);
        MinimumChurnSeconds = SessionBudgetMath.MinimumDiscriminatingChurnSeconds(options.Rate, SessionCeiling);
        RelayReceiveBufferBudget = SessionBudgetMath.RelayReceiveBufferBudget(SessionCeiling, relayReceiveBufferBytes);
        RetentionDiscriminating = SessionBudgetMath.NonDiscriminatingChurnWindow(options.Rate, options.ChurnSeconds, SessionCeiling) is null;
    }

    /// <summary>The arrival jitter the retention ceiling absorbs.</summary>
    internal int SteadyStateMargin { get; }

    /// <summary>The live-population bound <c>rate × (idle + 2 × sweep) + margin</c>.</summary>
    internal int SessionCeiling { get; }

    /// <summary>The smallest churn window whose cumulative flow count exceeds <see cref="SessionCeiling"/>.</summary>
    internal int MinimumChurnSeconds { get; }

    /// <summary>The byte form of <see cref="SessionCeiling"/>: the estimated kernel receive buffer the retention window may hold.</summary>
    internal long RelayReceiveBufferBudget { get; }

    /// <summary>Whether the churn window outlasts the ceiling, i.e. whether the retention ceiling can discriminate retention from accumulation at all.</summary>
    internal bool RetentionDiscriminating { get; }

    /// <summary>The steady-state samples the acceptance read; zero means the churn window never spanned a retention window.</summary>
    internal int SteadyStateSamples { get; private set; }

    /// <summary>
    /// The largest live population observed over the steady window at arrival granularity. The 5 s rows
    /// sample the retention sawtooth and can miss its peak by up to one sample interval of arrivals; the
    /// retention ceiling is asserted against this measurement instead.
    /// </summary>
    internal int SteadyPeakSessions { get; private set; }

    /// <summary>The steady-state row with the largest live population, or null when there was none: the sampled half of the series, and the only instants that carry a descriptor and managed-memory reading.</summary>
    internal SessionBudgetSample? WorstSteady { get; private set; }

    /// <summary>The smallest descriptor-budget headroom over the steady-state samples; null when there was no steady-state sample.</summary>
    internal long? DescriptorHeadroom { get; private set; }

    /// <summary>Whether the live population and the estimated receive buffer held the retention bound.</summary>
    internal bool RetentionBounded { get; private set; }

    internal IReadOnlyList<string> Failures => _failures;

    internal bool Passed => _failures.Count == 0;

    /// <summary>
    /// Runs every assertion over one finished run and records the verdict. The failure list
    /// is written with the summary row before the run throws, so a failed soak still records its
    /// evidence.
    /// </summary>
    internal void Evaluate(SessionBudgetOutcome outcome)
    {
        var steady = SteadySamples(outcome.Samples);
        SteadyStateSamples = steady.Count;
        WorstSteady = WorstSteadySample(steady);
        SteadyPeakSessions = outcome.SteadyPeakSessions;
        AssertChurn(outcome);
        AssertRetention();
        AssertDescriptorBudget(steady);
        AssertDrain(outcome.Final);
    }

    private void AssertChurn(SessionBudgetOutcome outcome)
    {
        var attempted = outcome.Accepted + outcome.Rejected;
        if (outcome.Rejected != 0)
        {
            _failures.Add(string.Create(CultureInfo.InvariantCulture, $"The churn window refused {outcome.Rejected} of {attempted} new flows at capacity {_options.Capacity}."));
        }

        if (outcome.ChurnPeakSessions >= _options.Capacity)
        {
            _failures.Add(string.Create(CultureInfo.InvariantCulture, $"The live session count peaked at {outcome.ChurnPeakSessions}, not strictly below the {_options.Capacity} capacity."));
        }

        if (outcome.Final.CapacityRejections != 0)
        {
            _failures.Add(string.Create(CultureInfo.InvariantCulture, $"The run recorded {outcome.Final.CapacityRejections} UDP capacity rejection(s) at capacity {_options.Capacity}; the acceptance requires a capacity that never binds."));
        }

        if (outcome.Final.SetupRejections != 0)
        {
            _failures.Add(string.Create(CultureInfo.InvariantCulture, $"The run recorded {outcome.Final.SetupRejections} setup-queue rejection(s); the setup ring never drained fast enough."));
        }

        if (outcome.Final.SetupFailures != 0)
        {
            _failures.Add(string.Create(CultureInfo.InvariantCulture, $"The run recorded {outcome.Final.SetupFailures} UDP session setup failure(s); every new flow must establish."));
        }

        if (outcome.Final.DatagramsLost != 0)
        {
            _failures.Add(string.Create(CultureInfo.InvariantCulture, $"The churn window lost {outcome.Final.DatagramsLost} of {outcome.Final.DatagramsSent} datagrams: {outcome.Accepted} flows were accepted and {outcome.Final.DatagramsReceived} echoes came back."));
        }
    }

    /// <summary>
    /// The retention half: the churn window must be able to discriminate retention from accumulation
    /// before the population, the descriptor-scaled shape, or the estimated kernel receive buffer may
    /// claim the bound. The population is the arrival-granular steady peak (the 5 s rows can miss the
    /// sawtooth's peak by up to a sample interval of arrivals) and the buffer is the byte form of the
    /// same ceiling; its per-session ratio must reproduce the configured buffer, so the reported
    /// estimate cannot drift away from the population it is derived from.
    /// </summary>
    private void AssertRetention()
    {
        RetentionBounded = false;
        if (SessionBudgetMath.NonDiscriminatingChurnWindow(_options.Rate, _options.ChurnSeconds, SessionCeiling) is { } refusal)
        {
            _failures.Add(refusal);
            return;
        }

        if (WorstSteady is not { } worst)
        {
            _failures.Add(string.Create(CultureInfo.InvariantCulture, $"The churn window never reached steady state: no sample at or after idle + sweep = {(_idleTimeout + _sweepInterval).TotalSeconds:0} s, so the retention ceiling would be vacuous; raise --churn-seconds above {(_idleTimeout + _sweepInterval).TotalSeconds:0}."));
            return;
        }

        if (SteadyPeakSessions > SessionCeiling)
        {
            _failures.Add(string.Create(CultureInfo.InvariantCulture, $"Steady state peaked at {SteadyPeakSessions} live sessions, above the retention ceiling {SessionCeiling} = {_options.Rate} flows/s × (idle {_idleTimeout.TotalSeconds:0} s + 2 × sweep {_sweepInterval.TotalSeconds:0} s) + margin {SteadyStateMargin}: the population is accumulating instead of following the active flow set."));
            return;
        }

        if (worst.RelayReceiveBufferBytes > RelayReceiveBufferBudget)
        {
            _failures.Add(string.Create(CultureInfo.InvariantCulture, $"The estimated kernel receive buffer at t={worst.ElapsedSeconds:F1} s was {worst.RelayReceiveBufferBytes} bytes for {worst.Sessions} live sessions, above the retention bound {RelayReceiveBufferBudget} = ceiling {SessionCeiling} × {_relayReceiveBufferBytes} bytes: the estimate does not follow the bounded population."));
            return;
        }

        if (worst.Sessions > 0 && Math.Abs(worst.RelayReceiveBufferBytesPerSession - _relayReceiveBufferBytes) > 0.5)
        {
            _failures.Add(string.Create(CultureInfo.InvariantCulture, $"The estimated kernel receive buffer at t={worst.ElapsedSeconds:F1} s was {worst.RelayReceiveBufferBytesPerSession:F0} bytes per live session, not the configured {_relayReceiveBufferBytes}: the estimate must be live sessions × the configured relay receive buffer."));
            return;
        }

        RetentionBounded = true;
    }

    /// <summary>
    /// The per-session descriptor budget: every steady-state sample must fit the shipped shape's two
    /// descriptors per live session — one relay socket and one control connection — which the budget
    /// charges as one relay socket per session with <see cref="SessionBudgetMath.DescriptorSlackPerSession"/>
    /// slack plus the control connections the sample's own <c>associations</c> column carries (the
    /// harness server's observation, or the one-per-session allowance when it ran out of process).
    /// The worst (smallest) headroom is kept for the summary row.
    /// </summary>
    private void AssertDescriptorBudget(List<SessionBudgetSample> steady)
    {
        DescriptorHeadroom = null;
        if (steady.Count == 0) return;
        var worstHeadroom = long.MaxValue;
        var worst = steady[0];
        foreach (var sample in steady)
        {
            var headroom = DescriptorHeadroomOf(sample);
            if (headroom >= worstHeadroom) continue;
            worstHeadroom = headroom;
            worst = sample;
        }

        DescriptorHeadroom = worstHeadroom;
        if (worstHeadroom < 0)
        {
            var delta = worst.FileDescriptors - worst.BaselineFileDescriptors;
            var charge = worst.ControlConnectionsCharged;
            var budget = SessionBudgetMath.FileDescriptorBudget(worst.Sessions, charge) + DescriptorMargin;
            _failures.Add(string.Create(CultureInfo.InvariantCulture, $"The descriptor delta at t={worst.ElapsedSeconds:F1} s was {delta} for {worst.Sessions} live sessions and {charge} control connection(s) ({worst.FileDescriptorsPerSession:F2} descriptors per session), above the budget {budget}: one relay socket per session with {SessionBudgetMath.DescriptorSlackPerSession:F2} slack plus one control connection per live association (the harness server's own live count when it ran in this process, otherwise the one-per-session allowance), so a leaked socket or a control connection that does not follow its session's teardown does not fit."));
        }
    }

    /// <summary>
    /// The drain half: no live session, no control connection the harness SOCKS5 server still sees, and
    /// the descriptor count back at its pre-churn baseline. The association term reads the server's own
    /// count, so it fires on a flow whose control connection outlived its session even when the session
    /// count is already zero — a subject the sessions term cannot show — and it is left unevaluated when
    /// the server ran out of process and nobody reported the count (<c>associations</c> is null).
    /// </summary>
    private void AssertDrain(SessionBudgetSample final)
    {
        if (final.Sessions != 0)
        {
            _failures.Add(string.Create(CultureInfo.InvariantCulture, $"The {_options.DrainSeconds} s drain window ended with {final.Sessions} live sessions; retention releases a silent flow after idle + sweep, so --drain-seconds must cover idle + 2 × sweep."));
        }

        if (final.Associations is { } liveControls && liveControls != 0)
        {
            _failures.Add(string.Create(CultureInfo.InvariantCulture, $"The drain ended with {liveControls} control connection(s) the harness SOCKS5 server still saw open against {final.Sessions} live session(s): an association lives and dies with its flow, so a connection the server still holds outlived the session that dialed it."));
        }

        var delta = final.FileDescriptors - final.BaselineFileDescriptors;
        if (delta > DescriptorMargin)
        {
            _failures.Add(string.Create(CultureInfo.InvariantCulture, $"The drain returned {final.FileDescriptors} descriptors against the pre-churn baseline {final.BaselineFileDescriptors} (+{delta}, allowed +{DescriptorMargin}): a relay socket or control connection is leaking."));
        }
    }

    private List<SessionBudgetSample> SteadySamples(IReadOnlyList<SessionBudgetSample> samples)
    {
        var threshold = (_idleTimeout + _sweepInterval).TotalSeconds;
        var steady = new List<SessionBudgetSample>();
        foreach (var sample in samples)
        {
            if (sample.Phase is "churn" or "churnEnd" && sample.ElapsedSeconds >= threshold) steady.Add(sample);
        }

        return steady;
    }

    private static SessionBudgetSample? WorstSteadySample(List<SessionBudgetSample> steady)
    {
        SessionBudgetSample? worst = null;
        foreach (var sample in steady)
        {
            if (worst is null || sample.Sessions > worst.Sessions) worst = sample;
        }

        return worst;
    }

    private static long DescriptorHeadroomOf(SessionBudgetSample sample) =>
        SessionBudgetMath.FileDescriptorBudget(sample.Sessions, sample.ControlConnectionsCharged) + DescriptorMargin - (sample.FileDescriptors - sample.BaselineFileDescriptors);
}
