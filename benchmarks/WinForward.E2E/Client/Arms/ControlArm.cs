using System.Globalization;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Client.Arms;

/// <summary>
/// The <c>"base"</c> kind: the no-product control, which runs the latency arm and then the loss arm back
/// to back inside one record. The type is named for the record it produces -- the control -- while the
/// plan kind keeps the name its plans and records publish.
/// </summary>
internal static class ControlArm
{
    // BASE is the no-product control: a latency phase and then a loss phase, back to back. Each
    // phase keeps its own default load; a plan parameter the BASE entry declares overrides both.
    private const int DefaultLatencyRatePerSecond = 20;
    private const int DefaultLatencyPayloadBytes = 120;
    internal const int DefaultLossRatePerSecond = 500;
    private const int DefaultLossPayloadBytes = 200;

    private static readonly string[] s_phases = ["latency", "loss"];

    internal static async Task<ArmOutcome> RunAsync(ArmContext context)
    {
        var spec = context.Spec;
        var phaseSeconds = spec.Seconds;
        var latencyContext = context.WithSpec(LatencyPhaseSpec(spec));
        var lossContext = context.WithSpec(LossPhaseSpec(spec));

        var startTicks = Clock.Now;
        var latency = await LatencyArm.RunAsync(latencyContext).ConfigureAwait(false);
        var loss = await LossArm.RunAsync(lossContext).ConfigureAwait(false);
        var elapsedTicks = Clock.Now - startTicks;

        var outcome = BuildOutcome(latency, loss, phaseSeconds, elapsedTicks);
        outcome.Notes.Add("base runs the latency arm and then the loss arm back to back inside one record, with no proxifier loaded: it is the harness floor.");
        outcome.Notes.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"base is two {phaseSeconds} s phases, so parameters.seconds is their sum and parameters.latency/parameters.loss carry the effective per-phase parameters; metrics.elapsedSeconds is the measured wall time."));
        outcome.Notes.Add("both phases take every load parameter the plan's BASE entry declares and fall back per phase only where it declares none.");
        outcome.Notes.Add(string.Concat(
            "the latency phase runs the plan's protocol (",
            latencyContext.Spec.Protocol,
            ") and the loss phase always runs udp, so a BASE entry that wants a udp latency floor has to declare protocol."));
        outcome.Notes.AddRange(latency.Notes);
        outcome.Notes.AddRange(loss.Notes);
        return outcome;
    }

    /// <summary>
    /// The record the two finished phases publish: the phase parameters and metrics one level down,
    /// the elapsed wall time, and the gates the phases' own outcomes reported.
    /// </summary>
    private static ArmOutcome BuildOutcome(ArmOutcome latency, ArmOutcome loss, double phaseSeconds, long elapsedTicks)
    {
        // The record spans both phases, so the declared seconds must be their sum: a consumer that
        // derives an offered or achieved rate from parameters.seconds would otherwise be short by
        // the whole second phase.
        return new ArmOutcome
        {
            Parameters = new ArmParameters
            {
                Seconds = phaseSeconds * 2,
                PhaseSeconds = phaseSeconds,
                Phases = s_phases,
                Latency = latency.Parameters,
                Loss = loss.Parameters,
            },
            Metrics = new ControlMetrics
            {
                ElapsedSeconds = NumberFormat.Round(Clock.ToSeconds(elapsedTicks), 4),

                // Both arms always publish their own typed record, so each phase contract is the
                // narrowing rather than a check.
                Latency = (LatencyMetrics)latency.Metrics,
                Loss = (LossMetrics)loss.Metrics,
            },
            Gates =
            {
                [ArmKeys.Common.Gates.ClientSendLoss] = Sum(latency.Gates, loss.Gates, ArmKeys.Common.Gates.ClientSendLoss),
                [ArmKeys.Common.Gates.WindowOverflow] = Sum(latency.Gates, loss.Gates, ArmKeys.Common.Gates.WindowOverflow),
                [ArmKeys.Common.Gates.BacklogDrops] = Sum(latency.Gates, loss.Gates, ArmKeys.Common.Gates.BacklogDrops),
                [ArmKeys.Common.Gates.SendFailures] = Sum(latency.Gates, loss.Gates, ArmKeys.Common.Gates.SendFailures),
                [ArmKeys.Common.Gates.LaneShortfall] = Sum(latency.Gates, loss.Gates, ArmKeys.Common.Gates.LaneShortfall),
                [ArmKeys.Common.Gates.ScheduleTruncated] = Math.Max(
                    latency.Gates.GetValueOrDefault(ArmKeys.Common.Gates.ScheduleTruncated),
                    loss.Gates.GetValueOrDefault(ArmKeys.Common.Gates.ScheduleTruncated)),
                [ArmKeys.Common.Gates.InFlightCeilingMs] = Math.Max(
                    latency.Gates.GetValueOrDefault(ArmKeys.Common.Gates.InFlightCeilingMs),
                    loss.Gates.GetValueOrDefault(ArmKeys.Common.Gates.InFlightCeilingMs)),
                [ArmKeys.Common.Gates.WindowMs] = loss.Gates.GetValueOrDefault(ArmKeys.Common.Gates.WindowMs),
            },
        };

        // The two phases publish different subsets of the shared gate set: a gate the phase did not
        // publish is a zero, because a gate the arm has no quantity for is not a missing measurement.
        static double Sum(Dictionary<string, double> first, Dictionary<string, double> second, string key) =>
            first.GetValueOrDefault(key) + second.GetValueOrDefault(key);
    }

    // The latency phase keeps its own default load because it is a probe and the loss phase because
    // it is a flood; a parameter the plan's BASE entry declares overrides both.
    private static ArmSpec LatencyPhaseSpec(ArmSpec spec) => new()
    {
        Name = $"{spec.Name}-latency",
        Kind = "latency",
        Seconds = spec.Seconds,
        RatePerSecond = spec.RatePerSecond > 0 ? spec.RatePerSecond : DefaultLatencyRatePerSecond,
        PayloadBytes = spec.PayloadBytes > 0 ? spec.PayloadBytes : DefaultLatencyPayloadBytes,
        Protocol = spec.Protocol,
        Window = spec.Window,
        Lanes = spec.Lanes,
    };

    private static ArmSpec LossPhaseSpec(ArmSpec spec) => new()
    {
        Name = $"{spec.Name}-loss",
        Kind = "loss",
        Seconds = spec.Seconds,
        RatePerSecond = spec.RatePerSecond > 0 ? spec.RatePerSecond : DefaultLossRatePerSecond,
        PayloadBytes = spec.PayloadBytes > 0 ? spec.PayloadBytes : DefaultLossPayloadBytes,
        Protocol = "udp",
        Window = spec.Window,
        LossWindowMs = spec.LossWindowMs,
    };
}
